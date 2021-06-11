﻿using Milki.Extensions.Audio.NAudioExtensions;
using Milki.Extensions.Audio.NAudioExtensions.Wave;
using Milki.Extensions.Audio.Utilities;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Milki.Extensions.Audio.Subchannels
{
    public abstract class MultiElementsChannel : Subchannel, ISoundElementsProvider
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly VariableStopwatch _sw = new VariableStopwatch();

        protected List<SoundElement> SoundElements;
        public ReadOnlyCollection<SoundElement> SoundElementCollection => new ReadOnlyCollection<SoundElement>(SoundElements);
        protected readonly SingleMediaChannel ReferenceChannel;
        private ConcurrentQueue<SoundElement> _soundElementsQueue;

        private VolumeSampleProvider _volumeProvider;
        private bool _isVolumeEnabled = false;

        private Task _playingTask;
        //private Task _calibrationTask;
        private CancellationTokenSource _cts;
        private readonly object _skipLock = new object();

        private BalanceSampleProvider _sliderSlideBalance;
        private VolumeSampleProvider _sliderSlideVolume;
        private BalanceSampleProvider _sliderAdditionBalance;
        private VolumeSampleProvider _sliderAdditionVolume;
        private MemoryStream _lastSliderStream;
        private float _playbackRate;

        public bool IsPlayRunning => _playingTask != null &&
                                     !_playingTask.IsCanceled &&
                                     !_playingTask.IsCompleted &&
                                     !_playingTask.IsFaulted;

        public override TimeSpan Duration { get; protected set; }

        public override TimeSpan Position => _sw.Elapsed;

        public override TimeSpan ChannelStartTime => TimeSpan.FromMilliseconds(Configuration.GeneralOffset);

        public int ManualOffset
        {
            get => -(int)_sw.ManualOffset.TotalMilliseconds;
            internal set => _sw.ManualOffset = -TimeSpan.FromMilliseconds(value);
        }

        public sealed override float PlaybackRate
        {
            get => _sw.Rate;
            protected set => _sw.Rate = value;
        }

        public sealed override bool UseTempo { get; protected set; }

        public float BalanceFactor { get; set; } = 0.35f;

        public MixingSampleProvider Submixer { get; protected set; }

        public MultiElementsChannel(AudioPlaybackEngine engine,
            bool enableVolume = true,
            SingleMediaChannel referenceChannel = null) : base(engine)
        {
            if (!enableVolume) Submixer = engine.RootMixer;
            ReferenceChannel = referenceChannel;
        }

        public override async Task Initialize()
        {
            if (Submixer == null)
            {
                Submixer = new MixingSampleProvider(WaveFormatFactory.IeeeWaveFormat)
                {
                    ReadFully = true
                };
                _volumeProvider = new VolumeSampleProvider(Submixer);
                _isVolumeEnabled = true;
                Engine.AddRootSample(_volumeProvider);
            }

            SampleControl.Volume = 1;
            SampleControl.Balance = 0;
            SampleControl.VolumeChanged = f =>
            {
                if (_volumeProvider != null) _volumeProvider.Volume = f;
            };

            await RequeueAsync(TimeSpan.Zero).ConfigureAwait(false);

            var ordered = SoundElements.OrderBy(k => k.Offset).ToArray();
            var last9Element = ordered.Skip(ordered.Length - 9).ToArray();
            var max = TimeSpan.FromMilliseconds(last9Element.Length == 0 ? 0 : last9Element.Max(k =>
                ((k.ControlType == SlideControlType.None) || (k.ControlType == SlideControlType.StartNew))
                ? k.NearlyPlayEndTime
                : 0
            ));

            Duration = MathEx.Max(
                TimeSpan.FromMilliseconds(SoundElements.Count == 0 ? 0 : SoundElements.Max(k => k.Offset)), max);

            await Task.Run(() =>
            {
                SoundElements
                    .Where(k => k.FilePath == null &&
                                (k.ControlType == SlideControlType.None || k.ControlType == SlideControlType.StartNew))
                    .AsParallel()
                    .WithDegreeOfParallelism(Environment.ProcessorCount > 1 ? Environment.ProcessorCount - 1 : 1)
                    .ForAll(k => CachedSound.CreateCacheSounds(new[] { k.FilePath }).Wait());
            }).ConfigureAwait(false);


            //await CachedSound.CreateCacheSounds(SoundElements
            //    .Where(k => k.FilePath != null)
            //    .Select(k => k.FilePath));

            await SetPlaybackRate(AppSettings.Default?.Play?.PlaybackRate ?? 1, AppSettings.Default?.Play?.PlayUseTempo ?? true)
                .ConfigureAwait(false);
            PlayStatus = PlayStatus.Ready;
        }

        public override async Task Play()
        {
            if (PlayStatus == PlayStatus.Playing) return;

            await ReadyLoopAsync().ConfigureAwait(false);

            StartPlayTask();
            RaisePositionUpdated(_sw.Elapsed, true);
            //StartCalibrationTask();
            PlayStatus = PlayStatus.Playing;
        }

        public override async Task Pause()
        {
            if (PlayStatus == PlayStatus.Paused) return;

            await CancelLoopAsync().ConfigureAwait(false);

            RaisePositionUpdated(_sw.Elapsed, true);
            PlayStatus = PlayStatus.Paused;
        }

        public override async Task Stop()
        {
            if (PlayStatus == PlayStatus.Paused && Position == TimeSpan.Zero) return;

            await CancelLoopAsync().ConfigureAwait(false);
            await SkipTo(TimeSpan.Zero).ConfigureAwait(false);
            PlayStatus = PlayStatus.Paused;
        }

        public override async Task Restart()
        {
            if (Position == TimeSpan.Zero) return;

            await SkipTo(TimeSpan.Zero).ConfigureAwait(false);
            await Play().ConfigureAwait(false);
        }

        public override async Task SkipTo(TimeSpan time)
        {
            if (time == Position) return;

            Submixer.RemoveMixerInput(_sliderSlideBalance);
            Submixer.RemoveMixerInput(_sliderAdditionBalance);

            await Task.Run(() =>
            {
                lock (_skipLock)
                {
                    var status = PlayStatus;
                    PlayStatus = PlayStatus.Reposition;

                    _sw.SkipTo(time);
                    Logger.Debug("{0} want skip: {1}; actual: {2}", Description, time, Position);
                    RequeueAsync(time).Wait();

                    PlayStatus = status;
                }
            }).ConfigureAwait(false);
            RaisePositionUpdated(_sw.Elapsed, true);
        }

        public override async Task Sync(TimeSpan time)
        {
            _sw.SkipTo(time);
            await Task.CompletedTask;
        }

        public override async Task SetPlaybackRate(float rate, bool useTempo)
        {
            PlaybackRate = rate;
            UseTempo = useTempo;
            AdjustModOffset();
            await Task.CompletedTask;
        }

        private void AdjustModOffset()
        {
            if (Math.Abs(_sw.Rate - 0.75) < 0.001 && !UseTempo)
                _sw.VariableOffset = TimeSpan.FromMilliseconds(-25);
            else if (Math.Abs(_sw.Rate - 1.5) < 0.001 && UseTempo)
                _sw.VariableOffset = TimeSpan.FromMilliseconds(15);
            else
                _sw.VariableOffset = TimeSpan.Zero;
        }

        private void StartPlayTask()
        {
            if (IsPlayRunning) return;

            _playingTask = new Task(async () =>
            {
                while (_soundElementsQueue.Count > 0)
                {
                    if (_cts.Token.IsCancellationRequested)
                    {
                        _sw.Stop();
                        break;
                    }

                    //Position = _sw.Elapsed;
                    RaisePositionUpdated(_sw.Elapsed, false);
                    lock (_skipLock)
                    {
                        // wow nothing here
                    }

                    await TakeElements((int)_sw.ElapsedMilliseconds);

                    if (!TaskEx.TaskSleep(1, _cts)) break;
                }

                if (!_cts.Token.IsCancellationRequested)
                {
                    PlayStatus = PlayStatus.Finished;
                    await SkipTo(TimeSpan.Zero).ConfigureAwait(false);
                }
            }, TaskCreationOptions.LongRunning);
            _playingTask.Start();
        }

        public async Task TakeElements(int offset)
        {
            while (_soundElementsQueue.TryPeek(out var soundElement) &&
                   soundElement.Offset <= offset &&
                   _soundElementsQueue.TryDequeue(out soundElement))
            {
                lock (_skipLock)
                {
                    // wow nothing here
                }

                try
                {
                    switch (soundElement.ControlType)
                    {
                        case SlideControlType.None:
                            var cachedSound = await soundElement.GetCachedSoundAsync().ConfigureAwait(false);
                            var flag = Submixer.PlaySound(cachedSound, soundElement.Volume,
                                soundElement.Balance * BalanceFactor);
                            if (soundElement.SubSoundElement != null)
                                soundElement.SubSoundElement.RelatedProvider = flag;

                            break;
                        case SlideControlType.StopNote:
                            if (soundElement.RelatedProvider != null)
                            {
                                Submixer.RemoveMixerInput(soundElement.RelatedProvider);
                                var fadeOut = new FadeInOutSampleProvider(soundElement.RelatedProvider);
                                fadeOut.BeginFadeOut(400);
                                Submixer.AddMixerInput(fadeOut);
                            }
                            break;
                        case SlideControlType.StartNew:
                            Submixer.RemoveMixerInput(_sliderSlideBalance);
                            Submixer.RemoveMixerInput(_sliderAdditionBalance);
                            cachedSound = await soundElement.GetCachedSoundAsync().ConfigureAwait(false);
                            _lastSliderStream?.Dispose();
                            if (cachedSound is null) continue;
                            var byteArray = new byte[cachedSound.AudioData.Length * sizeof(float)];
                            Buffer.BlockCopy(cachedSound.AudioData, 0, byteArray, 0, byteArray.Length);

                            _lastSliderStream = new MemoryStream(byteArray);
                            var myf = new RawSourceWaveStream(_lastSliderStream, cachedSound.WaveFormat);
                            var loop = new LoopStream(myf);
                            if (soundElement.HitsoundType.HasFlag(HitsoundTypeStore.Slide))
                            {
                                _sliderSlideVolume = new VolumeSampleProvider(loop.ToSampleProvider())
                                {
                                    Volume = soundElement.Volume
                                };
                                _sliderSlideBalance = new BalanceSampleProvider(_sliderSlideVolume)
                                {
                                    Balance = soundElement.Balance * BalanceFactor
                                };
                                Submixer.AddMixerInput(_sliderSlideBalance);
                            }
                            else if (soundElement.HitsoundType.HasFlag(HitsoundTypeStore.SlideWhistle))
                            {
                                _sliderAdditionVolume = new VolumeSampleProvider(loop.ToSampleProvider())
                                {
                                    Volume = soundElement.Volume
                                };
                                _sliderAdditionBalance = new BalanceSampleProvider(_sliderAdditionVolume)
                                {
                                    Balance = soundElement.Balance * BalanceFactor
                                };
                                Submixer.AddMixerInput(_sliderAdditionBalance);
                            }

                            break;
                        case SlideControlType.StopRunning:
                            Submixer.RemoveMixerInput(_sliderSlideBalance);
                            Submixer.RemoveMixerInput(_sliderAdditionBalance);
                            break;
                        case SlideControlType.ChangeBalance:
                            if (_sliderAdditionBalance != null)
                                _sliderAdditionBalance.Balance = soundElement.Balance * BalanceFactor;
                            if (_sliderSlideBalance != null)
                                _sliderSlideBalance.Balance = soundElement.Balance * BalanceFactor;
                            break;
                        case SlideControlType.ChangeVolume:
                            if (_sliderAdditionVolume != null) _sliderAdditionVolume.Volume = soundElement.Volume;
                            if (_sliderSlideVolume != null) _sliderSlideVolume.Volume = soundElement.Volume;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error while play target element. Source: {0}; ControlType",
                        soundElement.FilePath, soundElement.ControlType);
                }
            }
        }

        protected async Task RequeueAsync(TimeSpan startTime)
        {
            var queue = new ConcurrentQueue<SoundElement>();
            if (SoundElements == null)
            {
                var o = new List<SoundElement>(await GetSoundElements().ConfigureAwait(false));
                SoundElements = new List<SoundElement>(o.Concat(o.Where(k => k.SubSoundElement != null).Select(k => k.SubSoundElement)));
                Duration = TimeSpan.FromMilliseconds(SoundElements.Count == 0 ? 0 : SoundElements.Max(k => k.Offset));
                SoundElements.Sort(new SoundElementTimingComparer());
            }

            await Task.Run(() =>
            {
                foreach (var i in SoundElements)
                {
                    if (i.Offset < startTime.TotalMilliseconds)
                        continue;
                    queue.Enqueue(i);
                }
            }).ConfigureAwait(false);

            _soundElementsQueue = queue;
        }

        private async Task ReadyLoopAsync()
        {
            _cts = new CancellationTokenSource();
            _sw.Start();
            await Task.CompletedTask;
        }

        private async Task CancelLoopAsync()
        {
            _sw.Stop();
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            await TaskEx.WhenAllSkipNull(_playingTask/*, _calibrationTask*/).ConfigureAwait(false);
            Logger.Debug(@"{0} task canceled.", Description);
        }

        public abstract Task<IEnumerable<SoundElement>> GetSoundElements();

        public override async ValueTask DisposeAsync()
        {
            await Stop().ConfigureAwait(false);
            Logger.Debug($"Disposing: Stopped.");

            _cts?.Dispose();
            Logger.Debug($"Disposing: Disposed {nameof(_cts)}.");
            if (_volumeProvider != null)
                Engine.RemoveRootSample(_volumeProvider);
            //await base.DisposeAsync().ConfigureAwait(false);
            //Logger.Debug($"Disposing: Disposed base.");
        }
    }
}
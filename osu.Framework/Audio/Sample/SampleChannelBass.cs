// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using ManagedBass;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Track;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Logging;

namespace osu.Framework.Audio.Sample
{
    internal sealed class SampleChannelBass : SampleChannel, IBassAudioChannel, IBassAudio
    {
        private readonly SampleBass sample;
        private volatile int channel;

        /// <summary>
        /// Whether the channel is currently playing.
        /// </summary>
        /// <remarks>
        /// This is set to <c>true</c> immediately upon <see cref="Play"/>, but the channel may not be audibly playing yet.
        /// </remarks>
        public override bool Playing
        {
            get
            {
                // When short samples loop (especially within mixers), there's a small window where the ChannelIsActive state could be Stopped.
                // In order to not provide a "stale" value here, we'll not trust the internal playing state from BASS.
                if (Looping && userRequestedPlay)
                    return true;

                return playing || enqueuedPlaybackStart;
            }
        }

        private volatile bool playing;

        /// <summary>
        /// <c>true</c> if the user last called <see cref="Play"/>.
        /// <c>false</c> if the user last called <see cref="Stop"/>.
        /// </summary>
        private volatile bool userRequestedPlay;

        /// <summary>
        /// Whether the playback start has been enqueued.
        /// </summary>
        private volatile bool enqueuedPlaybackStart;

        public override bool Looping
        {
            get => base.Looping;
            set
            {
                base.Looping = value;
                setLoopFlag(Looping);
            }
        }

        private bool hasChannel => channel != 0;

        public override ChannelAmplitudes CurrentAmplitudes => (bassAmplitudeProcessor ??= new BassAmplitudeProcessor(this)).CurrentAmplitudes;

        private readonly BassRelativeFrequencyHandler relativeFrequencyHandler;
        private BassAmplitudeProcessor? bassAmplitudeProcessor;

        /// <summary>
        /// Creates a new <see cref="SampleChannelBass"/>.
        /// </summary>
        /// <param name="sample">The <see cref="SampleBass"/> to create the channel from.</param>
        public SampleChannelBass(SampleBass sample)
            : base(sample.Name)
        {
            this.sample = sample;

            relativeFrequencyHandler = new BassRelativeFrequencyHandler
            {
                FrequencyChangedToZero = stopInternal,
                FrequencyChangedFromZero = () =>
                {
                    // Only unpause if the channel has been played by the user.
                    if (userRequestedPlay)
                        playInternal();
                },
            };

            ensureChannel();
        }

        protected override void UpdateState()
        {
            if (hasChannel)
            {
                switch (bassMixer.ChannelIsActive(this))
                {
                    case PlaybackState.Playing:
                    // Stalled counts as playing, as playback will continue once more data has streamed in.
                    case PlaybackState.Stalled:
                    // The channel is in a "paused" state via zero-frequency. It should be marked as playing even if it's in a paused state internally.
                    case PlaybackState.Paused when userRequestedPlay:
                        playing = true;
                        break;

                    default:
                        playing = false;
                        break;
                }
            }
            else
            {
                // Channel doesn't exist - a rare case occurring as a result of device updates.
                playing = false;
            }

            base.UpdateState();

            bassAmplitudeProcessor?.Update();
        }

        public override void Play()
        {
            // Check if this channel is disposed first to not set enqueuedPlaybackStart to true, as it makes Playing true.
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            userRequestedPlay = true;

            // Pin Playing and IsAlive to true so that the channel isn't killed by the next update. This is only reset after playback is started.
            enqueuedPlaybackStart = true;

            // Bring this channel alive, allowing it to receive updates.
            base.Play();

            EnqueueAction(() =>
            {
                if (playInternal())
                    playing = true;

                enqueuedPlaybackStart = false;
            });
        }

        public override void Stop()
        {
            userRequestedPlay = false;

            base.Stop();

            EnqueueAction(() =>
            {
                // Clean up channel from mixer tracking when explicitly stopped
                // Check if mixer exists (might be null during disposal or if never added)
                if (Mixer != null)
                {
                    bassMixer.RemoveFromActiveChannels(this);
                    bassMixer.RemoveFromPendingChannels(this);
                }
                stopInternal();
                playing = false;
            });
        }

        internal override void OnStateChanged()
        {
            base.OnStateChanged();

            if (!hasChannel)
                return;

            Bass.ChannelSetAttribute(channel, ChannelAttribute.Volume, AggregateVolume.Value);
            Bass.ChannelSetAttribute(channel, ChannelAttribute.Pan, AggregateBalance.Value);
            relativeFrequencyHandler.SetFrequency(AggregateFrequency.Value);
        }

        private bool playInternal()
        {
            // Channel may have been freed via UpdateDevice().
            ensureChannel();

            if (!hasChannel)
                return false;

            // Ensure state is correct before starting.
            Logger.Log($"[SampleChannelBass:{Name}] playInternal() - Invalidating state");
            InvalidateState();

            // Bass will restart the sample if it has reached its end. This behavior isn't desirable so block locally.
            // Unlike TrackBass, sample channels can't have sync callbacks attached, so the stopped state is used instead
            // to indicate the natural stoppage of a sample as a result of having reaching the end.
            if (Played && bassMixer.ChannelIsActive(this) == PlaybackState.Stopped)
            {
                Logger.Log($"[SampleChannelBass:{Name}] playInternal() - Sample already played and stopped, returning false");
                return false;
            }

            if (relativeFrequencyHandler.IsFrequencyZero)
            {
                Logger.Log($"[SampleChannelBass:{Name}] playInternal() - Frequency is zero, returning true");
                return true;
            }

            Logger.Log($"[SampleChannelBass:{Name}] playInternal() - Calling AddToBassMixAndPlay, channel={channel}, mixer.Handle={bassMixer.Handle}");
            bool result = bassMixer.AddToBassMixAndPlay(this);
            Logger.Log($"[SampleChannelBass:{Name}] playInternal() - AddToBassMixAndPlay returned {result}");
            return result;
        }

        private void stopInternal()
        {
            if (hasChannel)
                bassMixer.ChannelPause(this);
        }

        private void setLoopFlag(bool value) => EnqueueAction(() =>
        {
            if (hasChannel)
                Bass.ChannelFlags(channel, value ? BassFlags.Loop : BassFlags.Default, BassFlags.Loop);
        });

        private void ensureChannel() => EnqueueAction(() =>
        {
            Logger.Log($"[SampleChannelBass:{Name}] ensureChannel() called - hasChannel={hasChannel}");
            
            if (hasChannel)
            {
                Logger.Log($"[SampleChannelBass:{Name}] ensureChannel() - Already has channel, returning");
                return;
            }

            BassFlags flags = BassFlags.SampleChannelStream | BassFlags.Decode;

            // While this shouldn't cause issues, we've had a small subset of users reporting issues on windows.
            // To keep things working let's only apply to other platforms until we know more.
            // See https://github.com/ppy/osu/issues/18652.
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                flags |= BassFlags.AsyncFile;

            Logger.Log($"[SampleChannelBass:{Name}] ensureChannel() - Creating channel from sample {sample.SampleId}");
            channel = Bass.SampleGetChannel(sample.SampleId, flags);
            
            Logger.Log($"[SampleChannelBass:{Name}] ensureChannel() - Created channel: {channel}, LastError: {Bass.LastError}");

            if (!hasChannel)
            {
                Logger.Log($"[SampleChannelBass:{Name}] ensureChannel() - FAILED to create channel!");
                return;
            }

            Logger.Log($"[SampleChannelBass:{Name}] ensureChannel() - Setting loop flag and frequency handler");
            setLoopFlag(Looping);
            relativeFrequencyHandler.SetChannel(channel);
            
            Logger.Log($"[SampleChannelBass:{Name}] ensureChannel() - Channel ready");
        });

        #region Mixing

        private BassAudioMixer bassMixer => (BassAudioMixer)Mixer.AsNonNull();

        bool IBassAudioChannel.IsActive => IsAlive;

        int IBassAudioChannel.Handle => channel;

        bool IBassAudioChannel.MixerChannelPaused { get; set; } = true;

        BassAudioMixer IBassAudioChannel.Mixer => bassMixer;

        bool IBassAudioChannel.IsPlaybackRequested => userRequestedPlay;

        void IBassAudio.UpdateDevice(int deviceIndex)
        {
            Logger.Log($"[SampleChannelBass:{Name}] UpdateDevice({deviceIndex}) called - userRequestedPlay={userRequestedPlay}, hasChannel={hasChannel}, Mixer={Mixer?.Identifier}");
            
            if (userRequestedPlay)
            {
                EnqueueAction(() =>
                {
                    Logger.Log($"[SampleChannelBass:{Name}] UpdateDevice enqueued action executing");
                    
                    // This channel handle will become stale (valid but no sound) with device switch & mixer recreate
                    // We clean it up and get a new one
                    // So that samples (especially looping ones) would continue playing after device switch
                    if (Mixer != null)
                    {
                        Logger.Log($"[SampleChannelBass:{Name}] Removing from mixer tracking");
                        bassMixer.RemoveFromActiveChannels(this);
                        bassMixer.RemoveFromPendingChannels(this);
                    }
                    else
                    {
                        Logger.Log($"[SampleChannelBass:{Name}] Mixer is null, skipping removal");
                    }
                    
                    Logger.Log($"[SampleChannelBass:{Name}] Resetting channel handle (was {channel})");
                    channel = 0;
                    
                    Logger.Log($"[SampleChannelBass:{Name}] Calling ensureChannel()...");
                    ensureChannel();

                    if (hasChannel)
                    {
                        Logger.Log($"[SampleChannelBass:{Name}] Channel recreated ({channel}), calling playInternal()...");
                        bool result = playInternal();
                        Logger.Log($"[SampleChannelBass:{Name}] playInternal() returned {result}");
                    }
                    else
                    {
                        Logger.Log($"[SampleChannelBass:{Name}] Failed to recreate channel!");
                    }
                });
            }
            else
            {
                Logger.Log($"[SampleChannelBass:{Name}] Skipping UpdateDevice - userRequestedPlay is false");
            }
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (hasChannel)
            {
                bassMixer.StreamFree(this);
                channel = 0;
            }

            playing = false;

            base.Dispose(disposing);
        }
    }
}

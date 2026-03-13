// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManagedBass;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Audio
{
    /// <summary>
    /// Coordinates audio device switching with proper async/await for complex operations.
    /// </summary>
    internal class AudioDeviceSwitchCoordinator(AudioManager manager, AudioThread thread)
    {
        private readonly AudioManager audioManager = manager;
        private readonly AudioThread audioThread = thread;

        /// <summary>
        /// Switches audio device with proper coordination for ASIO or standard backends.
        /// </summary>
        public async Task SwitchDeviceAsync(int deviceId, AudioBackendType backendType)
        {
            Logger.Log($"[AudioDeviceSwitchCoordinator] Starting device switch to device {deviceId}, backend {backendType}");

            if (backendType == AudioBackendType.Asio)
            {
                await coordinatedSwitchAsync(deviceId, backendType).ConfigureAwait(false);
            }
            else
            {
                await standardSwitchAsync(deviceId, backendType).ConfigureAwait(false);
            }

            Logger.Log($"[AudioDeviceSwitchCoordinator] Device switch to device {deviceId} completed");
        }

        /// <summary>
        /// Coordinated async switch for ASIO or complex device changes.
        /// Each phase is awaited to ensure completion before next phase.
        /// </summary>
        private async Task coordinatedSwitchAsync(int deviceId, AudioBackendType backendType)
        {
            Logger.Log("[AudioDeviceSwitchCoordinator] Starting coordinated switch");

            // Phase 1: Stop all channels
            Logger.Log("[AudioDeviceSwitchCoordinator] Phase 1: Stopping all channels");
            await stopAllChannelsAsync().ConfigureAwait(false);

            // Phase 2: Dispose old channels
            Logger.Log("[AudioDeviceSwitchCoordinator] Phase 2: Disposing old channels");
            await disposeChannelsAsync().ConfigureAwait(false);

            // Phase 3: Switch device
            Logger.Log("[AudioDeviceSwitchCoordinator] Phase 3: Switching device");
            await switchDeviceCoreAsync(deviceId, backendType).ConfigureAwait(false);

            // Phase 4: Recreate channels
            Logger.Log("[AudioDeviceSwitchCoordinator] Phase 4: Recreating channels");
            await recreateChannelsAsync().ConfigureAwait(false);

            // Phase 5: Resume playback
            Logger.Log("[AudioDeviceSwitchCoordinator] Phase 5: Resuming playback");
            await resumePlaybackAsync().ConfigureAwait(false);

            Logger.Log("[AudioDeviceSwitchCoordinator] Coordinated switch completed successfully");
        }

        /// <summary>
        /// Runs an action on the audio thread and returns the result asynchronously.
        /// This bridges the gap between async coordination and audio thread requirements.
        /// </summary>
        private async Task<T> runOnAudioThreadAsync<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>();
#pragma warning disable CS4014
            audioManager.EnqueueAction(() =>
            {
                try
                {
                    T result = action();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
#pragma warning restore CS4014
            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Runs an action on the audio thread asynchronously (void return).
        /// </summary>
        private async Task runOnAudioThreadAsync(Action action)
        {
            await runOnAudioThreadAsync(() =>
            {
                action();
                return true;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Standard device switch using existing traversal (fallback for Bass/WASAPI).
        /// </summary>
        private async Task standardSwitchAsync(int deviceId, AudioBackendType backendType)
        {
            Logger.Log($"[AudioDeviceSwitchCoordinator] Standard switch to device {deviceId}, backend {backendType}");
            Logger.Log($"[AudioDeviceSwitchCoordinator] Current device: {audioManager.CurrentDeviceIndex}, Current backend: {audioManager.CurrentBackendType}");

            // For standard switches, we use the existing TrySetDevice method
            // MUST run on audio thread!
            bool success = await runOnAudioThreadAsync(() => audioManager.TrySetDevice(deviceId)).ConfigureAwait(false);

            if (success)
            {
                Logger.Log($"[AudioDeviceSwitchCoordinator] TrySetDevice succeeded");
                Logger.Log($"[AudioDeviceSwitchCoordinator] Active mixers count: {audioManager.ActiveMixers.Count}");

                // Check mixer states
                foreach (var mixer in audioManager.ActiveMixers)
                {
                    if (mixer is BassAudioMixer bassMixer)
                    {
                        Logger.Log($"[AudioDeviceSwitchCoordinator] Mixer '{bassMixer.Identifier}' - Handle: {bassMixer.Handle}, Active channels: {bassMixer.activeChannels.Count}");
                    }
                }
            }
            else
            {
                Logger.Log($"[AudioDeviceSwitchCoordinator] TrySetDevice FAILED", LoggingTarget.Runtime, LogLevel.Error);
            }

            Logger.Log($"[AudioDeviceSwitchCoordinator] Standard switch completed");
        }

        private async Task stopAllChannelsAsync()
        {
            var tasks = new List<Task>();

            foreach (var mixer in audioManager.ActiveMixers)
            {
                if (mixer is BassAudioMixer bassMixer)
                {
                    foreach (var channel in bassMixer.activeChannels.ToArray())
                    {
                        if (channel is IBassAudioChannel bassChannel && bassChannel.IsPlaybackRequested)
                        {
                            // Stop the channel
                            if (channel is Sample.ISampleChannel sampleChannel)
                            {
                                sampleChannel.Stop();
                                tasks.Add(Task.CompletedTask); // Stop is fire-and-forget
                            }
                            else if (channel is Track.ITrack track)
                            {
                                tasks.Add(track.StopAsync());
                            }
                        }
                    }
                }
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            Logger.Log($"[AudioDeviceSwitchCoordinator] Stopped {tasks.Count} channels");
        }

        private async Task disposeChannelsAsync()
        {
            // Channels are disposed when they're recreated
            // For now, just ensure they're cleaned up from mixer
            Logger.Log("[AudioDeviceSwitchCoordinator] Disposing channels (via recreation)");
            await Task.CompletedTask; // Placeholder
        }

        private async Task switchDeviceCoreAsync(int deviceId, AudioBackendType backendType)
        {
            Logger.Log($"[AudioDeviceSwitchCoordinator] switchDeviceCoreAsync - device {deviceId}, backend {backendType}");

            // MUST run on audio thread!
            bool success = await runOnAudioThreadAsync(() => audioManager.TrySetDevice(deviceId)).ConfigureAwait(false);

            if (!success)
            {
                Logger.Log($"[AudioDeviceSwitchCoordinator] Failed to switch to device {deviceId}", LoggingTarget.Runtime, LogLevel.Error);
                throw new InvalidOperationException($"Failed to switch to device {deviceId}");
            }

            Logger.Log($"[AudioDeviceSwitchCoordinator] Switched to device {deviceId} successfully");
        }

        private async Task recreateChannelsAsync()
        {
            // Channels will be recreated on next Play() or UpdateDevice
            // For now, mark them as needing recreation
            Logger.Log("[AudioDeviceSwitchCoordinator] Channels will be recreated on next operation");
            await Task.CompletedTask; // Placeholder
        }

        private async Task resumePlaybackAsync()
        {
            // Playback will resume when UpdateDevice is called on each channel
            // This happens through the normal UpdateDevice flow
            Logger.Log("[AudioDeviceSwitchCoordinator] Playback resumption handled by channel UpdateDevice");
            await Task.CompletedTask; // Placeholder
        }
    }

    /// <summary>
    /// Types of audio backends supported.
    /// </summary>
    public enum AudioBackendType
    {
        /// <summary>
        /// Standard BASS backend (DirectSound on Windows, default on other platforms).
        /// </summary>
        Bass,

        /// <summary>
        /// WASAPI backend (Windows only).
        /// </summary>
        Wasapi,

        /// <summary>
        /// ASIO backend (Windows only, low-latency).
        /// </summary>
        Asio
    }
}

// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Logging;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osuTK;

namespace osu.Framework.Tests.Visual.Audio
{
    public partial class TestSceneAudioDeviceConfigAsync : FrameworkTestScene
    {
        private const string default_placeholder = "Default (System)";

        [Resolved]
        private AudioManager? audioManager { get; set; }

        [Resolved]
        private ISampleStore? sampleStore { get; set; }

        [Resolved]
        private ITrackStore? trackStore { get; set; }

        private Sample? sample;
        private SampleChannel? channel;
        private Track? track;

        private BasicButton playStopButton = null!;
        private BasicButton trackPlayStopButton = null!;
        private BasicDropdown<string> deviceDropdown = null!;
        private BasicCheckbox wasapiCheckbox = null!;
        private SpriteText statusText = null!;

        private bool isPlaying;
        private bool isUpdatingDropdown; // Flag to prevent handler from firing during programmatic updates

        public TestSceneAudioDeviceConfigAsync()
        {
            Child = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(20),
                Spacing = new Vector2(10),
                Children = new Drawable[]
                {
                    playStopButton = new BasicButton
                    {
                        Position = new Vector2(0, 50),
                        Width = 100,
                        Height = 30,
                        Text = "Play Sample",
                        Action = togglePlayback
                    },
                    trackPlayStopButton = new BasicButton
                    {
                        Position = new Vector2(110, 50),
                        Width = 100,
                        Height = 30,
                        Text = "Play Track",
                        Action = toggleTrackPlayback
                    },
                    deviceDropdown = new BasicDropdown<string>
                    {
                        Position = new Vector2(0, 90),
                        Width = 300,
                    },
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (audioManager == null || sampleStore == null)
                return;

            var flow = (FillFlowContainer)Child;
            bool isWindows = RuntimeInfo.OS == RuntimeInfo.Platform.Windows;

            if (isWindows)
            {
                flow.Add(new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 30,
                    Child = wasapiCheckbox = new BasicCheckbox
                    {
                        AutoSizeAxes = Axes.Both,
                        LabelText = "Use WASAPI (Experimental)"
                    }
                });

                wasapiCheckbox.Current.Value = audioManager.UseExperimentalWasapi.Value;

                // Use scheduled binding to ensure checkbox UI updates happen on the game thread
                wasapiCheckbox.Current.ValueChanged += _ =>
                {
                    if (audioManager == null) return;
                    audioManager.UseExperimentalWasapi.Value = wasapiCheckbox.Current.Value;
                };
                audioManager.UseExperimentalWasapi.ValueChanged += e => Schedule(() => wasapiCheckbox.Current.Value = e.NewValue);
            }

            flow.Add(statusText = new SpriteText
            {
                Text = getStatusText(),
                Font = FrameworkFont.Regular.With(size: 14)
            });

            audioManager.OnNewDevice += onDeviceChanged;
            audioManager.OnLostDevice += onDeviceChanged;

            AddStep("audio device config", audioDeviceConfig);

            // When user selects from dropdown, trigger device switch via AudioManager
            deviceDropdown.Current.ValueChanged += _ =>
            {
                if (audioManager == null || isUpdatingDropdown) return;

                string selected = deviceDropdown.Current.Value;

                // Use AudioDevice.Value to trigger the switch - AudioManager will resolve the device name
                // Empty string for default device, otherwise use the selected name
                audioManager.AudioDevice.Value = selected == default_placeholder ? string.Empty : selected;
            };

            // When AudioDevice changes externally (e.g., config load), update dropdown display
            audioManager.AudioDevice.ValueChanged += _ => Schedule(updateStatus);
            audioManager.UseExperimentalWasapi.ValueChanged += _ => Schedule(updateStatus);
        }

        private void onDeviceChanged(string _)
        {
            Schedule(audioDeviceConfig);
        }

        private void audioDeviceConfig()
        {
            if (audioManager == null || deviceDropdown == null)
                return;

            isUpdatingDropdown = true;

            // Use a visible placeholder for default device (empty string is not visible in dropdown)
            var deviceItems = new[] { default_placeholder }.Concat(audioManager.AudioDeviceNames).Distinct().ToList();
            deviceDropdown.Items = deviceItems;

            // Map between display value and actual AudioDevice value
            if (string.IsNullOrEmpty(audioManager.AudioDevice.Value))
                deviceDropdown.Current.Value = default_placeholder;
            else if (deviceItems.Contains(audioManager.AudioDevice.Value))
                deviceDropdown.Current.Value = audioManager.AudioDevice.Value;
            else if (deviceItems.Count > 0)
                deviceDropdown.Current.Value = deviceItems[0];

            isUpdatingDropdown = false;
        }

        private void togglePlayback()
        {
            if (sampleStore == null)
                return;

            if (isPlaying)
            {
                channel?.Stop();
                channel?.Dispose();
                channel = null;
                isPlaying = false;
            }
            else
            {
                sample ??= sampleStore.Get("tone.wav");
                channel = sample.GetChannel();
                channel.Looping = true;
                channel.Volume.Value = 0.5f;
                channel.Play();
                isPlaying = true;
            }

            playStopButton.Text = isPlaying ? "Stop Sample" : "Play Sample";
            updateStatus();
        }

        private bool isTrackPlaying;

        private void toggleTrackPlayback()
        {
            if (trackStore == null)
                return;

            if (isTrackPlaying)
            {
                track?.Stop();
                track?.Dispose();
                track = null;
                isTrackPlaying = false;
            }
            else
            {
                track ??= trackStore.Get("sample-track.mp3");
                if (track == null)
                    return;
                track.Looping = true;
                track.Volume.Value = 0.5f;
                track.Start();
                isTrackPlaying = true;
            }

            trackPlayStopButton.Text = isTrackPlaying ? "Stop Track" : "Play Track";
            updateStatus();
        }

        private void updateStatus()
        {
            if (statusText == null || audioManager == null)
                return;

            statusText.Text = getStatusText();
        }

        private string getStatusText()
        {
            if (audioManager == null)
                return "Audio manager not available";

            var currentDevice = string.IsNullOrEmpty(audioManager.AudioDevice.Value)
                ? "(Default)"
                : audioManager.AudioDevice.Value;

            string backendName = audioManager.CurrentBackendType switch
            {
                AudioBackendType.Wasapi => "WASAPI",
                AudioBackendType.Asio => "ASIO",
                _ => "DirectSound"
            };

            return $"Backend: {backendName} | Device: {currentDevice} | Sample: {isPlaying} | Track: {isTrackPlaying}";
        }

        protected override void Dispose(bool isDisposing)
        {
            if (audioManager != null)
            {
                audioManager.OnNewDevice -= onDeviceChanged;
                audioManager.OnLostDevice -= onDeviceChanged;
            }

            channel?.Stop();
            channel?.Dispose();
            sample?.Dispose();
            track?.Stop();
            track?.Dispose();
            base.Dispose(isDisposing);
        }
    }
}

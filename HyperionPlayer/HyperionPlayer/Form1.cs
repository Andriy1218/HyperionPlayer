using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using VkNet;
using VkNet.Enums;
using VkNet.Enums.Filters;
using VkNet.Model.Attachments;

namespace HyperionPlayer
{
    public partial class Form1 : Form
    {
        private VkApi vk;
        private readonly Icon playIcon = new Icon("play.ico");
        private readonly Icon pauseIcon = new Icon("pause.ico");
        private bool isPlayingNow;
        private BufferedWaveProvider bufferedWaveProvider;
        private IWavePlayer waveOut;
        private volatile StreamingPlaybackState playbackState;
        private volatile bool fullyDownloaded;
        private HttpWebRequest webRequest;
        private VolumeWaveProvider16 volumeProvider;
        delegate void ShowErrorDelegate(string message);
        double currentDuration;

        private enum StreamingPlaybackState
        {
            Stopped,
            Playing,
            Buffering,
            Paused
        }

        public Form1()
        {
            InitializeComponent();
            volumeSlider1.VolumeChanged += OnVolumeSliderChanged;
            Disposed += MP3StreamingPanel_Disposing;
            buttonPlayPause.Image = playIcon.ToBitmap();
            notifyIcon1.ContextMenuStrip = contextMenuStrip1;
            notifyIcon1.Icon = playIcon;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            Start:
            try
            {
                const int appId = 4990627;
                const string login = "";
                var pass = GetPass();
                var scope = Settings.All;

                vk = new VkApi();
                vk.Authorize(appId, login, pass, scope);

                GetFriends();
                if (vk.UserId != null) ShowAudioById(vk.UserId.Value);
            }
            catch (Exception)
            {
                goto Start;
            }
        }
        private string GetPass()
        {
            var s = "";
            return s;
        }

        private void GetFriends()
        {
            if (vk.UserId != null)
            {
                friendsListBox.Items.Add(new VKFriend("----      My Audio      ----", vk.UserId.Value));
                var friends = vk.Friends.Get(vk.UserId.Value, ProfileFields.FirstName | ProfileFields.LastName | ProfileFields.CanSeeAudio, 300, 0);
                foreach (var friend in friends.Where(friend => friend.CanSeeAudio))
                {
                    friendsListBox.Items.Add(new VKFriend(friend.FirstName + " " + friend.LastName, friend.Id));
                }
            }
        }

        private void ShowAudioById(long id)
        {
            audioListBox.Items.Clear();
            var audio = vk.Audio.Get(id);
            ShowAudio(audio);
        }

        private void ShowAudio(ReadOnlyCollection<Audio> audio)
        {
            Invoke((MethodInvoker)delegate
           {
               audioListBox.Items.Clear();
               foreach (Audio track in audio)
               {
                   audioListBox.Items.Add(new VKAudio(track.Title + " - " + track.Artist, track.Url.ToString(), track.Duration));
               }
           });
        }

        private void searchButton_Click(object sender, EventArgs e)
        {
            int count;
            var audio = vk.Audio.Search(searchQueryTextBox.Text, out count, true, AudioSort.Popularity, false, 100);
            ShowAudio(audio);
        }

        private void friendsListBox_Click(object sender, EventArgs e)
        {
            ShowAudioById(((VKFriend)friendsListBox.SelectedItem).Id);
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (FormWindowState.Minimized == WindowState)
            {
                Hide();
                notifyIcon1.ShowBalloonTip(500, "Audio", "Playing: " + titleTextBox.Text, ToolTipIcon.Info);
            }
        }

        private void notifyIcon1_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            buttonPlayPause_Click(sender, e);
        }

        private void OnVolumeSliderChanged(object sender, EventArgs e)
        {
            if (volumeProvider != null)
            {
                volumeProvider.Volume = volumeSlider1.Volume;
            }
        }

        private void ShowError(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new ShowErrorDelegate(ShowError), message);
            }
            else
            {
                MessageBox.Show(message);
            }
        }

        private void StreamMp3(object state)
        {
            fullyDownloaded = false;
            var url = (string)state;
            webRequest = (HttpWebRequest)WebRequest.Create(url);
            HttpWebResponse resp;
            try
            {
                resp = (HttpWebResponse)webRequest.GetResponse();
            }
            catch (WebException e)
            {
                if (e.Status != WebExceptionStatus.RequestCanceled)
                {
                    ShowError(e.Message);
                }
                return;
            }
            var buffer = new byte[16384 * 4]; // needs to be big enough to hold a decompressed frame

            IMp3FrameDecompressor decompressor = null;
            try
            {
                using (var responseStream = resp.GetResponseStream())
                {
                    var readFullyStream = new ReadFullyStream(responseStream);
                    do
                    {
                        if (IsBufferNearlyFull)
                        {
                            Thread.Sleep(3600);
                        }
                        else
                        {
                            Mp3Frame frame;
                            try
                            {
                                frame = Mp3Frame.LoadFromStream(readFullyStream);
                                if (frame == null) throw new EndOfStreamException();
                            }
                            catch (EndOfStreamException)
                            {
                                fullyDownloaded = true;
                                break;
                            }
                            catch (WebException)
                            {
                                break;
                            }
                            if (decompressor == null)
                            {
                                decompressor = CreateFrameDecompressor(frame);
                                bufferedWaveProvider = new BufferedWaveProvider(decompressor.OutputFormat)
                                {
                                    BufferDuration = TimeSpan.FromSeconds(15)
                                };
                            }
                            int decompressed = decompressor.DecompressFrame(frame, buffer, 0);
                            bufferedWaveProvider.AddSamples(buffer, 0, decompressed);
                        }

                    } while (playbackState != StreamingPlaybackState.Stopped);
                    decompressor?.Dispose();
                }
            }
            finally
            {
                decompressor?.Dispose();
            }
        }

        private static IMp3FrameDecompressor CreateFrameDecompressor(Mp3Frame frame)
        {
            WaveFormat waveFormat = new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2,
                frame.FrameLength, frame.BitRate);
            return new AcmMp3FrameDecompressor(waveFormat);
        }

        private bool IsBufferNearlyFull => bufferedWaveProvider != null &&
                                           bufferedWaveProvider.BufferLength - bufferedWaveProvider.BufferedBytes
                                           < bufferedWaveProvider.WaveFormat.AverageBytesPerSecond / 4;

        private void NextTrack(object sender, StoppedEventArgs e)
        {
            nextTrackButton_Click(sender, e);
        }

        private void audioListBox_MouseClick(object sender, MouseEventArgs e)
        {
            StopPlayback();
            isPlayingNow = false;
            buttonPlayPause_Click(sender, e);
        }

        private void buttonPlayPause_Click(object sender, EventArgs e)
        {
            if (audioListBox.SelectedItem == null) return;
            titleTextBox.Text = ((VKAudio)audioListBox.SelectedItem).Name;
            if (!isPlayingNow)
            {
                buttonPlayPause.Image = pauseIcon.ToBitmap();
                isPlayingNow = true;
                switch (playbackState)
                {
                    case StreamingPlaybackState.Stopped:
                        playbackState = StreamingPlaybackState.Buffering;
                        bufferedWaveProvider = null;
                        ThreadPool.QueueUserWorkItem(StreamMp3, ((VKAudio)audioListBox.SelectedItem).Url);
                        timer1.Enabled = true;
                        break;
                    case StreamingPlaybackState.Paused:
                        playbackState = StreamingPlaybackState.Buffering;
                        break;
                }
                notifyIcon1.Icon = pauseIcon;
            }
            else
            {
                if (playbackState != StreamingPlaybackState.Playing && playbackState != StreamingPlaybackState.Buffering)
                    return;
                isPlayingNow = false;
                buttonPlayPause.Image = playIcon.ToBitmap();
                waveOut?.Pause();
                playbackState = StreamingPlaybackState.Paused;
                notifyIcon1.Icon = playIcon;
            }
        }

        private void StopPlayback()
        {
            if (playbackState == StreamingPlaybackState.Stopped) return;
            currentDuration = 0;
            if (!fullyDownloaded)
            {
                webRequest.Abort();
            }
            playbackState = StreamingPlaybackState.Stopped;
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }
            timer1.Enabled = false;
            Thread.Sleep(500);
            ShowBufferState(0);
        }


        private void ShowBufferState(double totalSeconds)
        {
            Invoke((MethodInvoker)delegate
            {
                labelBuffered.Text = $"{totalSeconds:0.0}s";
                progressBarBuffer.Value = (int)(totalSeconds * 1000);
            });
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (playbackState == StreamingPlaybackState.Stopped) return;
            if (playbackState == StreamingPlaybackState.Playing && audioListBox.SelectedItem != null)
            {
                currentDuration += 0.260;
                durationTextBox.Text = VKAudio.SecondToString((int) currentDuration) + @"/" +
                                       VKAudio.SecondToString(((VKAudio) audioListBox.SelectedItem).Duration);
            }
            if (waveOut == null && bufferedWaveProvider != null)
            {
                waveOut = CreateWaveOut();
                waveOut.PlaybackStopped += OnPlaybackStopped;
                volumeProvider = new VolumeWaveProvider16(bufferedWaveProvider) { Volume = volumeSlider1.Volume };
                waveOut.Init(volumeProvider);
                progressBarBuffer.Maximum = (int)bufferedWaveProvider.BufferDuration.TotalMilliseconds;
            }
            else if (bufferedWaveProvider != null)
            {
                var bufferedSeconds = bufferedWaveProvider.BufferedDuration.TotalSeconds;
                ShowBufferState(bufferedSeconds);
                if (bufferedSeconds < 0.5 && playbackState == StreamingPlaybackState.Playing && !fullyDownloaded)
                {
                    Pause();
                }
                else if (bufferedSeconds > 3 && playbackState == StreamingPlaybackState.Buffering)
                {
                    Play();
                }
                else if (fullyDownloaded && Math.Abs(bufferedSeconds) < 0.01)
                {
                    StopPlayback();
                    NextTrack(sender, new StoppedEventArgs());
                }
            }
        }

        private void Play()
        {
            waveOut.Play();
            playbackState = StreamingPlaybackState.Playing;
        }

        private void Pause()
        {
            playbackState = StreamingPlaybackState.Buffering;
            waveOut.Pause();
        }

        private IWavePlayer CreateWaveOut()
        {
            return new WaveOut();
        }

        private void MP3StreamingPanel_Disposing(object sender, EventArgs e)
        {
            StopPlayback();
        }

        private void buttonStop_Click(object sender, EventArgs e)
        {
            StopPlayback();
            isPlayingNow = false;
            titleTextBox.Clear();
            audioListBox.ClearSelected();
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                MessageBox.Show($"Playback Error {e.Exception.Message}");
            }
        }

        private void nextTrackButton_Click(object sender, EventArgs e)
        {
            StopPlayback();
            isPlayingNow = false;
            audioListBox.SelectedIndex = audioListBox.SelectedIndex < (audioListBox.Items.Count - 1)
                ? audioListBox.SelectedIndex + 1
                : 0;
            buttonPlayPause_Click(sender, e);
        }

        private void previousTrackButton_Click(object sender, EventArgs e)
        {
            StopPlayback();
            isPlayingNow = false;
            audioListBox.SelectedIndex = audioListBox.SelectedIndex != 0
                ? audioListBox.SelectedIndex - 1
                : audioListBox.Items.Count - 1;
            buttonPlayPause_Click(sender, e);
        }
    }
}
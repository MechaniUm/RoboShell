using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using LogLib;

namespace RoboLogic
{
    

    public class UWPLocalSpeaker //: ISpeaker
    {
        public SpeechSynthesizer Synthesizer = new SpeechSynthesizer();
        public MediaElement Media { get; set; }

        private bool isPlaying = false;

        public UWPLocalSpeaker(MediaElement Media, VoiceGender G)
        {
            this.Media = Media;
            this.Media.AutoPlay = true;
            var v = (from x in SpeechSynthesizer.AllVoices
                     where (x.Gender == G && x.Language == "ru-RU")
                     select x).FirstOrDefault();
            if (v != null) Synthesizer.Voice = v;
        }

        public async Task Speak(string s) {
            BeforeLog("Speak", $"Text='{s}'");
            Log.Trace($"IN {GetType().Name}.Speak() Synthesize STARTED", Log.LogFlag.Debug);
            var x = await Synthesizer.SynthesizeTextToStreamAsync(s);
            Log.Trace($"IN {GetType().Name}.Speak() Synthesize FINISHED", Log.LogFlag.Debug);
            Media.SetSource(x, x.ContentType);
            Media.AutoPlay = true;
            Media.Play();
            AfterLog("Speak", $"Text='{s}'");
        }

        public async Task Speak(SpeechSynthesisStream s)
        {
            Media.SetSource(s, s.ContentType);
            Media.AutoPlay = true;
            Media.Play();
        }

        public void ShutUp() {
            Media.Stop();
        }

        public void Play(Uri audioUri)
        {
            Media.Source = audioUri;
            Media.AutoPlay = true;
            Media.Play();
        }

        public async Task Play(Uri audioUri, int duration) {

            Media.Source = audioUri;
            Media.AutoPlay = true;
            Media.Play();
            await Task.Delay(TimeSpan.FromMilliseconds(duration));
            Media.Stop();
        }

        public bool CanPlay() {
            return !isPlaying ;

        }

        public void BeforeLog(string method = "Execute", string argsString = null)
        {
            if (argsString == null)
            {
                Log.Trace($"BEFORE {GetType().Name}.{method}()", Log.LogFlag.Debug);
            }
            else
            {
                Log.Trace($"BEFORE {GetType().Name}.{method}(): {argsString}", Log.LogFlag.Debug);
            }
        }

        public void AfterLog(string method = "Execute", string argsString = null)
        {
            if (argsString == null)
            {
                Log.Trace($"AFTER {GetType().Name}.{method}()", Log.LogFlag.Debug);
            }
            else
            {
                Log.Trace($"AFTER {GetType().Name}.{method}(): {argsString}", Log.LogFlag.Debug);
            }
        }
    }
}

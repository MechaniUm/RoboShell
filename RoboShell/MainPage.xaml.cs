using Newtonsoft.Json;
using RoboLogic;
using RuleEngineNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Gpio;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using LogLib;

// Это приложение получает ваше изображение с веб-камеры и
// распознаёт эмоции на нём, обращаясь к Cognitive Services
// Предварительно с помощью Windows UWP API анализируется, есть
// ли на фотографии лицо.

// Эмоции затем сериализуются в формат JSON. Они становятся доступны
// в строке, помеченной TODO:

namespace RoboShell
{


    public sealed partial class MainPage : Page
    {

        MediaCapture MC;

        DispatcherTimer FaceWaitTimer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(2) };
        DispatcherTimer PreDropoutTimer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(6) };
        DispatcherTimer DropoutTimer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(15) };
        DispatcherTimer InferenceTimer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(1) };
        DispatcherTimer GpioTimer = new DispatcherTimer() { Interval = TimeSpan.FromMilliseconds(500) };
        DispatcherTimer ArduinoInputTimer = new DispatcherTimer() { Interval = TimeSpan.FromMilliseconds(500) };

        //EmotionServiceClient EmoAPI = new EmotionServiceClient(Config.EmotionAPIKey,Config.EmotionAPIEndpoint);
        //FaceServiceClient FaceAPI = new FaceServiceClient(Config.FaceAPIKey,Config.FaceAPIEndpoint);

        private static HttpClient httpClient = new HttpClient();

        FaceDetectionEffect FaceDetector;
        VideoEncodingProperties VideoProps;


        private GpioPin[] ArduinoPins;
        private readonly int[] ArduinoPinsNumbers = Config.InputPinsNumbers; //must change
        GpioController gpio;

        private void InitGpio()
        {
            gpio = GpioController.GetDefault();
            ArduinoPins = new GpioPin[ArduinoPinsNumbers.Length];
            if (gpio == null)
            {
                return;
            }

            for(int i = 0; i < ArduinoPinsNumbers.Length; i++)
            {
                ArduinoPins[i] = gpio.OpenPin(ArduinoPinsNumbers[i]);
                ArduinoPins[i].SetDriveMode(GpioPinDriveMode.Input);
            }

            LogLib.Log.Trace($"Gpio initialized correctly.");

        }

        bool IsFacePresent = false; // Shows the short-term state of the face in camera
        bool InDialog = false; // represents long-term state - are we in dialog, or waiting for user
        bool CaptureAfterEnd = false; // do face capture after speech ends

        RuleEngine RE;

        int BoringCounter = 60;

        public MainPage()
        {
            LogLib.Log.Trace("Logger was initialized");
            
            this.InitializeComponent();
        }


        /// <summary>
        /// Первоначальная инициализация страницы
        /// </summary>
        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await Init();
            Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await InitLongRunning());
        }

        private async void EndSpeech(object sender, RoutedEventArgs e)
        {
            if (CaptureAfterEnd)
            {
                CaptureAfterEnd = false;
                await RecognizeFace();
            }
        }

        // Function used to execute extensions commands of rule engine
        private async void ExExecutor(string Cmd, string Param)
        {
            Log.Trace($"BEFORE {GetType().Name}.ExExecutor(): Cmd='{Cmd}', Param='{Param}'", Log.LogFlag.Debug);
            switch (Cmd)
            {
                case "Recapture":
                    if (Param == "EndSpeech") CaptureAfterEnd = true;
                    else if (Param.StartsWith("After:"))
                    {
                        var t = Param.Split(':');
                        var v = double.Parse(t[1]);
                        var dt = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(v) };
                        dt.Tick +=
                            async (s, ea) => { dt.Stop(); await RecognizeFace(); };
                        dt.Start();
                    }
                    else await RecognizeFace();
                    break;
            }
            Log.Trace($"AFTER {GetType().Name}.ExExecutor(): Cmd='{Cmd}', Param='{Param}'", Log.LogFlag.Debug);
        }

        private void ArduinoInput(object sender, object e)
        {
            string input = "";
            for (int i = 0; i < ArduinoPinsNumbers.Length; ++i)
            {
                if (ArduinoPins[i].Read() == GpioPinValue.High)
                {
                    input += "1";
                } else
                {
                    input += "0";
                }
            }
            if (input != "0000")
            {
                if (Config.logArduino)
                {
                    LogLib.Log.Trace($"Received: {input}");
                }
            }
            if (RE.State.ContainsKey("isPlaying") && RE.State["isPlaying"] == "True" && (input == "0100" || input == "0010" || input == "0001")) RE.SetVar("ArduinoInput", "0000");
            else RE.SetVar("ArduinoInput", input);
        }

        private void KeyPressed(CoreWindow sender, KeyEventArgs args)
        {
            if (args.VirtualKey >= VirtualKey.Number0 &&
                args.VirtualKey <= VirtualKey.Number9)
            {
                var st = $"Key_{args.VirtualKey - VirtualKey.Number0}";
                //LogLib.Log.Trace($"Initiating event {st}");
                //RE.SetVar("Event", st);
                RE.SetVar("KeyboardIn", st);
//                RE.Step();
            }
            // S = print state
            if (args.VirtualKey == VirtualKey.S)
            {
                foreach(var x in RE.State)
                {
                    LogLib.Log.Trace($" > {x.Key} -> {x.Value}");
                }
            }
        }

        Random Rnd = new Random();

        private void InferenceStep(object sender, object e)
        {
            //if (media.CurrentState == MediaElementState.Playing) return;
            if (!InDialog) BoringCounter--;
            if (BoringCounter==0)
            {
                RE.SetVar("Event", "Ping");
                LogLib.Log.Trace("Ping event intiated");
                BoringCounter = Rnd.Next(Config.MinBoringSeconds, Config.MaxBoringSeconds);
            }
            var s = RE.StepUntilLongRunning();
        }

        /// <summary>
        /// Инициализирует работу с камерой и с локальным распознавателем лиц
        /// </summary>
        private async Task Init()
        {
            LogLib.Log.Trace("Initializing media...");
            MC = new MediaCapture();
            var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var camera = cameras.Last();
            var settings = new MediaCaptureInitializationSettings() { VideoDeviceId = camera.Id, StreamingCaptureMode = StreamingCaptureMode.Video};
            
        
            await MC.InitializeAsync(settings);
            var resolutions = MC.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.Photo).Select(x => x as VideoEncodingProperties).OrderBy(x => x.Height * x.Width);
            VideoEncodingProperties maxRes = resolutions.FirstOrDefault();
            for (int i = 0; i < resolutions.Count(); i++) {
                if (resolutions.ElementAt(i).Width >= 320) {
                    maxRes = resolutions.ElementAt(i);
                    break;
                }
            }
            await MC.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.Photo, maxRes);

            if (!Config.Headless)
            {
                ViewFinder.Source = MC;
            }

            // Create face detection
            var def = new FaceDetectionEffectDefinition();
            def.SynchronousDetectionEnabled = false;
            def.DetectionMode = FaceDetectionMode.HighPerformance;
            FaceDetector = (FaceDetectionEffect)(await MC.AddVideoEffectAsync(def, MediaStreamType.VideoPreview));
            FaceDetector.FaceDetected += FaceDetectedEvent;
            FaceDetector.DesiredDetectionInterval = TimeSpan.FromMilliseconds(100);
            FaceDetector.Enabled = true;
            LogLib.Log.Trace("Ready to start face recognition");
            await MC.StartPreviewAsync();
            LogLib.Log.Trace("Face Recognition Started");
            var props = MC.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
            VideoProps = props as VideoEncodingProperties;


        }



        private async Task InitLongRunning() {
            var spk = new UWPLocalSpeaker(media, Windows.Media.SpeechSynthesis.VoiceGender.Female);
            CoreWindow.GetForCurrentThread().KeyDown += KeyPressed;
            RE = BracketedRuleEngine.LoadBracketedKb(Config.KBFileName);
            RE.SetSpeaker(spk);
            RE.Initialize();
            RE.SetExecutor(ExExecutor);
            FaceWaitTimer.Tick += StartDialog;
            DropoutTimer.Tick += FaceDropout;
            PreDropoutTimer.Tick += PreDropout;
            InferenceTimer.Tick += InferenceStep;
            InitGpio();
            if (gpio != null)
            {
                ArduinoInputTimer.Tick += ArduinoInput;
                ArduinoInputTimer.Start();
            }
            media.MediaEnded += EndSpeech;

            InferenceTimer.Start();

        }

        /// <summary>
        /// Срабатывает при локальном обнаружении лица на фотографии.
        /// Рисует рамку и устанавливает переменную IsFacePresent=true
        /// </summary>
        private async void FaceDetectedEvent(FaceDetectionEffect sender, FaceDetectedEventArgs args)
        {
            Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await HighlightDetectedFaces(args.ResultFrame.DetectedFaces));
//            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HighlightDetectedFace(args.ResultFrame.DetectedFaces.FirstOrDefault()));
        }

        /// <summary>
        /// Отвечает за рисование прямоугольника вокруг лица
        /// </summary>
        /// <param name="face">Обнаруженное лицо</param>
        /// <returns></returns>
        private async Task HighlightDetectedFace(DetectedFace face) {
            double cx =0, cy=0;
            if (!Config.Headless)
            {
                cx = ViewFinder.ActualWidth / VideoProps.Width;
                cy = ViewFinder.ActualHeight / VideoProps.Height;
            }

            if (face == null)
            {
                if (!Config.Headless) FaceRect.Visibility = Visibility.Collapsed;
                FaceWaitTimer.Stop();
                if (IsFacePresent)
                {
                    IsFacePresent = false;
                    DropoutTimer.Start();
                    PreDropoutTimer.Start();
                }
            }
            else
            {
                DropoutTimer.Stop();
                PreDropoutTimer.Stop();
                if (!Config.Headless)
                {
                    FaceRect.Margin = new Thickness(cx * face.FaceBox.X, cy * face.FaceBox.Y, 0, 0);
                    FaceRect.Width = cx * face.FaceBox.Width;
                    FaceRect.Height = cy * face.FaceBox.Height;
                    FaceRect.Visibility = Visibility.Visible;
                }
                if (!IsFacePresent)
                {
                    IsFacePresent = true;
                    if (!InDialog) FaceWaitTimer.Start(); // wait for 3 seconds to make sure face stable
                }
            }
        }

        private async Task HighlightDetectedFaces(IReadOnlyList<DetectedFace> faces) {
            var tmp = (from face in faces orderby face.FaceBox.Width*face.FaceBox.Height descending select face).ToList();
            
            if (tmp.Any() && tmp[0].FaceBox.Width * tmp[0].FaceBox.Height > VideoProps.Width*VideoProps.Height*Config.biggestFaceRelativeSize){
                var biggest = tmp[0];
                int facesCnt = 1;
                for (; facesCnt < tmp.Count; facesCnt++) {
                    if (faces[facesCnt].FaceBox.Height * faces[facesCnt].FaceBox.Width * Config.facesRelation < biggest.FaceBox.Height * biggest.FaceBox.Width) {
                        break;
                    }
                }
                RE.SetVar("FaceCount", facesCnt.ToString());
                if (Config.analyzeOnlyOneFace) {
                    if (facesCnt == 1) {
                        Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HighlightDetectedFace(biggest));
                    }
                    else {
                        Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HighlightDetectedFace(null));
                    }
                }
                else {
                    Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HighlightDetectedFace(biggest));
                }
            }
            else {
                Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HighlightDetectedFace(null));
            }
        }

        void FaceDropout(object sender, object e)
        {
            DropoutTimer.Stop();
            PreDropoutTimer.Stop();
            InDialog = false;
            BoringCounter = Rnd.Next(Config.MinBoringSeconds, Config.MaxBoringSeconds);
            LogLib.Log.Trace("Face dropout initiated");
            InferenceTimer.Stop();
            RE.Reset();
            RE.SetVar("Event", "FaceOut");
            RE.Run();
            InferenceTimer.Start();
        }


        void PreDropout(object sender, object e)
        {
            PreDropoutTimer.Stop();
            LogLib.Log.Trace("Face PRE dropout initiated");
            RE.SetVar("Event", "FacePreOut");
        }

        async void StartDialog(object sender, object e)
        {
            Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => {
                if (!IsFacePresent) return;
                RE.SetVar("Event", "FaceIn");
                RE.Step();
                InDialog = true;
                LogLib.Log.Trace("Calling face recognition");
                var res = await RecognizeFace();
                if (res)
                {
                    LogLib.Log.Trace("Initiating FaceRecognized Event");
                    RE.SetVar("Event", "FaceRecognized");
                    RE.Step();
                    if (!InferenceTimer.IsEnabled) InferenceTimer.Start(); //TODO check
                }
            });
            
        }

        async Task<bool> RecognizeFace() {
            Log.Trace($"BEFORE {GetType().Name}.RecognizeFace()", Log.LogFlag.Debug);
            if (!IsFacePresent) {
                Log.Trace($"AFTER {GetType().Name}.RecognizeFace(): IsFacePresent='false'", Log.LogFlag.Debug);
                return false;
            }

            if (RE.State.ContainsKey("Event")) {
                if (RE.State["Event"] == "FacePreOut") {
                    RE.SetVar("Event", "FaceIn");
                }
            }

            FaceWaitTimer.Stop();

            var photoAsStream = new MemoryStream();
            await MC.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), photoAsStream.AsRandomAccessStream());

            byte[] photoAsByteArray = photoAsStream.ToArray();

            PhotoInfoDTO photoInfo = await ProcessPhotoAsync(photoAsByteArray, Config.RecognizeEmotions);
            if (photoInfo.FoundAndProcessedFaces) {
                RE.SetVar("FaceCount", photoInfo.FaceCountAsString);
                RE.SetVar("Gender", photoInfo.Gender);
                RE.SetVar("Age", photoInfo.Age);
                if (Config.RecognizeEmotions) {
                    RE.SetVar("Emotion", photoInfo.Emotion);
                }
                Log.Trace($"AFTER {GetType().Name}.RecognizeFace(): FaceCount='{RE.State.Eval("FaceCount")}', " +
                          $"Age='{RE.State.Eval("Age")}', Gender='{RE.State.Eval("Gender")}', Emotion='{RE.State.Eval("Emotion")}'", Log.LogFlag.Debug);
                return true;
            }
            else {
                FaceWaitTimer.Start();
                Log.Trace($"AFTER {GetType().Name}.RecognizeFace(): FaceCount='0'", Log.LogFlag.Debug);
                return false;
            }
        }


        async Task<PhotoInfoDTO> ProcessPhotoAsync(byte[] photoAsByteArray, bool recognizeEmotions) {
            Log.Trace($"BEFORE {GetType().Name}.ProcessPhotoAsync()", Log.LogFlag.Debug);
            PhotoToProcessDTO photoToProcessDTO = new PhotoToProcessDTO {
                PhotoAsByteArray = photoAsByteArray,
                RecognizeEmotions = recognizeEmotions
            };
            var json = JsonConvert.SerializeObject(photoToProcessDTO);

            PhotoInfoDTO photoInfoDTO;

            using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json")){
                try {
                    Log.Trace($"{GetType().Name}.ProcessPhotoAsync(): sent to network");
                    HttpResponseMessage response = await httpClient.PostAsync(Config.CognitiveEndpoint, content);
                    Log.Trace($"{GetType().Name}.ProcessPhotoAsync(): received a responce from network", Log.LogFlag.Debug);
                    if (response.StatusCode.Equals(HttpStatusCode.OK)) {
                        photoInfoDTO = JsonConvert.DeserializeObject<PhotoInfoDTO>(await response.Content.ReadAsStringAsync());
                    }
                    else {
                        Log.Trace($"{GetType().Name}.ProcessPhotoAsync(): No faces found and analyzed", Log.LogFlag.Debug);
                        photoInfoDTO = new PhotoInfoDTO {
                            FoundAndProcessedFaces = false
                        };
                    }
                } catch (Exception e) {
                    Log.Trace($"{GetType().Name}.ProcessPhotoAsync(): Error! Exception message: " + e.Message, Log.LogFlag.Error);
                    photoInfoDTO = new PhotoInfoDTO {
                        FoundAndProcessedFaces = false
                    };
                }
                
            }
            Log.Trace($"AFTER {GetType().Name}.ProcessPhotoAsync()", Log.LogFlag.Debug);
            return photoInfoDTO;
        }
    }

    class PhotoToProcessDTO {
        public byte[] PhotoAsByteArray { get; set; }
        public bool RecognizeEmotions { get; set; }
    }

    class PhotoInfoDTO {
        public string FaceCountAsString { get; set; }
        public string Gender { get; set; }
        public string Age { get; set; }
        public string Emotion { get; set; }
        public bool FoundAndProcessedFaces { get; set; }
    }
}


using RoboLogic;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Devices.Gpio;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml.Media;
using CsvHelper;
using RuleEngineNet;
using RuleEngineUtils;
using LogLib;
using UnidecodeSharpFork;

// ReSharper disable StringLastIndexOfIsCultureSpecific.1

// ReSharper disable StringIndexOfIsCultureSpecific.1

namespace RuleEngineUtils {
    public static class DispatcherTaskExtensions
    {
        public static async Task<T> RunTaskAsync<T>(this CoreDispatcher dispatcher,
            Func<Task<T>> func, CoreDispatcherPriority priority = CoreDispatcherPriority.Normal)
        {
            var taskCompletionSource = new TaskCompletionSource<T>();
            await dispatcher.RunAsync(priority, async () =>
            {
                try
                {
                    taskCompletionSource.SetResult(await func());
                }
                catch (Exception ex)
                {
                    taskCompletionSource.SetException(ex);
                }
            });
            return await taskCompletionSource.Task;
        }

        // There is no TaskCompletionSource<void> so we use a bool that we throw away.
        public static async Task RunTaskAsync(this CoreDispatcher dispatcher,
            Func<Task> func, CoreDispatcherPriority priority = CoreDispatcherPriority.Normal) =>
            await RunTaskAsync(dispatcher, async () => { await func(); return false; }, priority);
    }
}

namespace RuleEngineNet {
    public abstract class Action {
        public abstract void Execute(State S);
        public bool ActiveAfterExecution { get; set; } = false;
        public virtual bool LongRunning { get; } = false;
        public static Action LoadXml(XElement X) { // TODO add new actions
            switch (X.Name.LocalName) {
                case "Assign":
                    return Assign.Parse(X);
                case "Clear":
                    return Clear.Parse(X);
                case "Say":
                    return Say.Parse(X);
                case "Play":
                    return Play.Parse(X, 100);
                case "ShutUp":
                    return ShutUp.Parse(X);
                case "Extension":
                    return Extension.Parse(X);
                case "OneOf":
                    return new OneOf(from z in X.Elements() select Action.LoadXml(z));
                case "GPIO":
                    return GPIO.Parse(X);
                default:
                    throw new RuleEngineException("Unsupported action type");
            }
        }

        public void BeforeLog(string method = "Execute", string argsString = null)
        {
            if (argsString == null) {
                Log.Trace($"BEFORE {GetType().Name}.{method}()", Log.LogFlag.Debug);
            }
            else {
                Log.Trace($"BEFORE {GetType().Name}.{method}(): {argsString}", Log.LogFlag.Debug);
            }
        }

        public void AfterLog(string method = "Execute", string argsString = null)
        {
            if (argsString == null)
            {
                Log.Trace($"AFTER {GetType().Name}.{method}()", Log.LogFlag.Debug);
            }
            else {
                Log.Trace($"AFTER {GetType().Name}.{method}(): {argsString}", Log.LogFlag.Debug);
            }
        }

        public abstract void Initialize();
        public static Action ParseActionSequence(string actionsSequence) {
            // ReSharper disable once RedundantAssignment
            Action action = null;

            action = ParseAtomicAction(actionsSequence);
            if (action == null) {
                int firstOpeningBracePosition = actionsSequence.IndexOf("(");
                int[] closingBracesPositions = BracketedConfigProcessor.AllIndexesOf(actionsSequence, ")");
                if (firstOpeningBracePosition == -1 || closingBracesPositions.Length != 0) {
                    if (closingBracesPositions.Length > 0) {
                        for (int tryingClosingBraceIndex = 0; tryingClosingBraceIndex != closingBracesPositions.Length; tryingClosingBraceIndex++) {
                            int possibleExpression1Start = firstOpeningBracePosition + 1;
                            int possibleAction1End = closingBracesPositions[tryingClosingBraceIndex];
                            int possibleExpression1Length = possibleAction1End - possibleExpression1Start;

                            Action action1 = null;
                            if (possibleExpression1Length > 0) {
                                string possibleAction1Substring = actionsSequence.Substring(possibleExpression1Start, possibleExpression1Length);
                                action1 = ParseActionSequence(possibleAction1Substring);
                            }
                            if (action1 != null) {
                                string restOfString = actionsSequence.Substring(possibleAction1End);

                                if (!Regex.IsMatch(restOfString, @"^\s*\)\s*$")) {
                                    if (Regex.IsMatch(restOfString, @"^\s*\)\s*(and|AND).+$")) {
                                        string possibleAction2Substring =
                                            restOfString.Substring(restOfString.IndexOf("and") + "and".Length);
                                        Action action2 = ParseActionSequence(possibleAction2Substring);
                                        if (action2 != null) {
                                            action = new CombinedAction(new List<Action> {action1});
                                            if (action2 is OneOf) {
                                                ((CombinedAction)action).Actions.Add(action2);
                                            }
                                            else if (action2 is CombinedAction)
                                            {
                                                ((CombinedAction)action).Actions.AddRange(((CombinedAction)action2).Actions);
                                            }
                                            else {
                                                ((CombinedAction)action).Actions.Add(action2);
                                            }
                                            
                                        }
                                    }
                                    else if (Regex.IsMatch(restOfString, @"^\s*\)\s*(or|OR).+$")) {
                                        string possibleAction2Substring = restOfString.Substring(restOfString.IndexOf("or") + "or".Length);
                                        Action action2 = ParseActionSequence(possibleAction2Substring);
                                        if (action2 != null) {
                                            action = new OneOf(new List<Action> {action1});
                                            if (action2 is OneOf action2AsOneOf) {
                                                ((OneOf)action).Actions.AddRange(action2AsOneOf.Actions);
                                            }
                                            else {
                                                ((OneOf) action).Actions.Add(action2);
                                            }
                                        }
                                    }
                                }
                                else {
                                    action = action1;
                                }

                                if (action != null) {
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            return action;
        }

        private static Action ParseAtomicAction(string actionSequence) {
            string ASSIGNEMENT_STRING_REGEX = $"^(?<var>{BracketedConfigProcessor.VARNAME_REGEX_PATTERN})\\s*=\\s*\".*\"$";
            string ASSIGNEMENT_REGEX = $"^(?<var>{BracketedConfigProcessor.VARNAME_REGEX_PATTERN})\\s*=\\s*(?<value>\\S+)$";
            string CLEAR_REGEX = $"^clear\\s+\\$(?<var>{BracketedConfigProcessor.VARNAME_REGEX_PATTERN})$";
            string SAY_REGEX = $"^say\\s+((?<probability>\\d*)\\s+)?\".*\"$";
            string SAY_FAST_REGEX = $"^sayFast\\s+((?<probability>\\d*)\\s+)?\".*\"$";
            string SHUT_UP_REGEX = $"^shutUp$";
            string GPIO_REGEX = $"^GPIO\\s+((?<probability>\\d*)\\s+)?(?<signal>([10],)*[10])\\s+(?<time>\\d+)$";
            string EXTERNAL_ACTION_NAME_REGEX_PATTERN = BracketedConfigProcessor.VARNAME_REGEX_PATTERN;
            string EXTERNAL_REGEX = $"^ext:(?<method>{EXTERNAL_ACTION_NAME_REGEX_PATTERN})\\s+\".*\"$";
            string PLAY_DELAY_REGEX = $"^play\\s+((?<probability>\\d*)\\s+)?\".*\"(\\s+(?<time>\\d+))?$";
            string STAY_ACTIVE_REGEX = $"^stayActive$";
            string COMPARE_ANSWERS_REGEX = $"^compareAnswers\\s+(?<goodAnswer>{BracketedConfigProcessor.VARNAME_REGEX_PATTERN})\\s+(?<realAnswer>{BracketedConfigProcessor.VARNAME_REGEX_PATTERN})$";
            string QUIZ_REGEX = $"^\\s*quiz\\s+(\\\"(?<filename>.*)\\\"\\s*)((?<randomOrder>randomOrder)\\s*)?((?<length>\\d+\\:\\d+))?\\s*$";
            Action action = null;

            string prettyActionSequence = actionSequence.Trim();
            int probability;
            try {
                if (Regex.IsMatch(prettyActionSequence, ASSIGNEMENT_STRING_REGEX)) {
                    int firstQuotePosition = prettyActionSequence.IndexOf("\"");
                    int lastQuotePosition = prettyActionSequence.LastIndexOf("\"");
                    int start = firstQuotePosition + 1;
                    int len = lastQuotePosition - start;
                    string possibleString = prettyActionSequence.Substring(start, len);
                    Match m = Regex.Match(prettyActionSequence, ASSIGNEMENT_STRING_REGEX);
                    if (BracketedConfigProcessor.AssertValidString(possibleString))
                    {
                        if (m.Length != 0)
                        {
                            action = new Assign(m.Groups["var"].Value, possibleString);
                        }
                    }
                    
                }
                else if (Regex.IsMatch(prettyActionSequence, ASSIGNEMENT_REGEX)) {
                    Match m = Regex.Match(prettyActionSequence, ASSIGNEMENT_REGEX);
                    if (m.Length != 0) {
                        action = new Assign(m.Groups["var"].Value, m.Groups["value"].Value);
                    }
                }
                else if (Regex.IsMatch(prettyActionSequence, CLEAR_REGEX)) {
                    Match m = Regex.Match(prettyActionSequence, CLEAR_REGEX);
                    if (m.Length != 0) {
                        action = new Clear(m.Groups["var"].Value);
                    }
                }
                else if (Regex.IsMatch(prettyActionSequence, SAY_REGEX)) {
                    int firstQuotePosition = prettyActionSequence.IndexOf("\"");
                    int lastQuotePosition = prettyActionSequence.LastIndexOf("\"");
                    int start = firstQuotePosition + 1;
                    int len = lastQuotePosition - start;
                    string possibleString = prettyActionSequence.Substring(start, len);
                    Match m = Regex.Match(prettyActionSequence, SAY_REGEX);
                    if (m.Length != 0 && m.Groups["probability"].Value.Length != 0)
                    {
                        probability = Int32.Parse(m.Groups["probability"].Value);
                    }
                    else
                    {
                        probability = 100;
                    }
                    if (BracketedConfigProcessor.AssertValidString(possibleString)) {
                        action = new Say(possibleString, probability);
                    }
                }
                else if (Regex.IsMatch(prettyActionSequence, SAY_FAST_REGEX))
                {
                    int firstQuotePosition = prettyActionSequence.IndexOf("\"");
                    int lastQuotePosition = prettyActionSequence.LastIndexOf("\"");
                    int start = firstQuotePosition + 1;
                    int len = lastQuotePosition - start;
                    string possibleString = prettyActionSequence.Substring(start, len);
                    Match m = Regex.Match(prettyActionSequence, SAY_REGEX);
                    if (m.Length != 0 && m.Groups["probability"].Value.Length != 0)
                    {
                        probability = Int32.Parse(m.Groups["probability"].Value);
                    }
                    else
                    {
                        probability = 100;
                    }
                    if (BracketedConfigProcessor.AssertValidString(possibleString))
                    {
                        action = new SayFast(possibleString, probability);
                    }
                }
                else if (Regex.IsMatch(prettyActionSequence, SHUT_UP_REGEX)) {
                    action = new ShutUp();
                }
                else if (Regex.IsMatch(prettyActionSequence, STAY_ACTIVE_REGEX))
                {
                    action = new StayActive();
                }
                else if (Regex.IsMatch(prettyActionSequence, COMPARE_ANSWERS_REGEX))
                {
                    Match m = Regex.Match(prettyActionSequence, COMPARE_ANSWERS_REGEX);
                    if (m.Length != 0)
                    {
                        action = new CompareAnswers(m.Groups["goodAnswer"].Value, m.Groups["realAnswer"].Value);
                    }
                }
                else if (Regex.IsMatch(prettyActionSequence, PLAY_DELAY_REGEX))
                {
                    int firstQuotePosition = prettyActionSequence.IndexOf("\"");
                    int lastQuotePosition = prettyActionSequence.LastIndexOf("\"");
                    int start = firstQuotePosition + 1;
                    int len = lastQuotePosition - start;
                    string possibleString = prettyActionSequence.Substring(start, len);
                    Match m = Regex.Match(prettyActionSequence, PLAY_DELAY_REGEX);
                    if (m.Length != 0 && m.Groups["probability"].Value.Length != 0)
                    {
                        probability = Int32.Parse(m.Groups["probability"].Value);
                    }
                    else
                    {
                        probability = 100;
                    }
                    Match m2 = Regex.Match(prettyActionSequence, PLAY_DELAY_REGEX);
                    if (m2.Length != 0 && m2.Groups["time"].Value.Length != 0)
                    {
                        var time = Int32.Parse(m.Groups["time"].Value);
                        if (BracketedConfigProcessor.AssertValidString(possibleString))
                        {
                            action = new Play(possibleString, probability, time);
                        }
                    }
                    else {
                        if (BracketedConfigProcessor.AssertValidString(possibleString))
                        {
                            action = new Play(possibleString, probability);
                        }
                    }
                }
                else if (Regex.IsMatch(prettyActionSequence, QUIZ_REGEX)) {
                    int firstQuotePosition = prettyActionSequence.IndexOf("\"");
                    int lastQuotePosition = prettyActionSequence.LastIndexOf("\"");
                    int start = firstQuotePosition + 1;
                    int len = lastQuotePosition - start;

                    string possibleString = prettyActionSequence.Substring(start, len);
                    if (BracketedConfigProcessor.AssertValidString(possibleString))
                    {
                        action = new Quiz(possibleString);

                        Match m = Regex.Match(prettyActionSequence, QUIZ_REGEX);

                        ((Quiz)action).randomOrdered = m.Length != 0 && m.Groups["randomOrder"].Value.Length != 0;

                        //                    Match m = Regex.Match(prettyActionSequence, QUIZ_REGEX);
                        if (m.Length != 0 && m.Groups["length"].Value.Length != 0)
                        {
                            var lengths = m.Groups["length"].Value.Split(':');
                            ((Quiz)action).lengthLowerBound = int.Parse(lengths[0]);
                            ((Quiz)action).lengthUpperBound = int.Parse(lengths[1]);
                        }
                    }

                    
                    
                    
                }
                else if (Regex.IsMatch(prettyActionSequence, GPIO_REGEX))
                {
                    Match m = Regex.Match(prettyActionSequence, GPIO_REGEX);
                    if (m.Length != 0 && m.Groups["probability"].Value.Length != 0)
                    {
                        probability = Int32.Parse(m.Groups["probability"].Value);
                    }
                    else
                    {
                        probability = 100;
                    }
                    if (m.Length != 0) {
                        action = new GPIO(m.Groups["signal"].Value.Split(',', ' ')
                            .Select(Int32.Parse).ToList(), Int32.Parse(m.Groups["time"].Value),
                            probability);
                    }
                }
                else if (Regex.IsMatch(prettyActionSequence, EXTERNAL_REGEX)) {
                    int firstQuotePosition = prettyActionSequence.IndexOf("\"");
                    int lastQuotePosition = prettyActionSequence.LastIndexOf("\"");
                    int start = firstQuotePosition + 1;
                    int len = lastQuotePosition - start;
                    string possibleString = prettyActionSequence.Substring(start, len);
                    if (BracketedConfigProcessor.AssertValidString(possibleString)) {
                        Match m = Regex.Match(prettyActionSequence, EXTERNAL_REGEX);
                        if (m.Length != 0) {
                            action = new Extension(m.Groups["method"].Value, possibleString);
                        }
                    }
                }
            }
            catch {
                action = null;
            }

            return action;
        }

        private static string illegal = " *:/?\\\"<>|+.!@%,";

        public string decode(string obj)
        {
            var res = obj.Unidecode();
            foreach (var c in illegal)
            {
                res = res.Replace(c, '_');
            }

            return res.ToLower().Trim('_').Replace("__", "_");
        }

    }


    public class Assign : Action {
        public string Var { get; set; }
        public Expression Expr { get; set; }
        public string Value { get; set; }

        public Assign(string Var, string Value) {
            this.Value = Value;
            this.Var = Var;
        }

        public override void Execute(State S) {
            BeforeLog();
            if (Expr != null) S.Assign(Var, Expr.Eval(S));
            if (Value != null) S.Assign(Var, S.EvalString(Value));
            AfterLog();
        }

        public override void Initialize() {
            return;
        }

        public static Assign Parse(XElement X) {
            return new Assign(X.Attribute("Var").Value, X.Attribute("Value").Value);
        }
    }

    public class Clear : Action {
        public string Var { get; set; }

        public Clear(string Var) {
            this.Var = Var;
        }
        public override void Initialize()
        {
            return;
        }

        public override void Execute(State S) {
            BeforeLog();
            S.Remove(Var);
            AfterLog();
        }

        public static Clear Parse(XElement X) {
            return new Clear(X.Attribute("Var").Value);
        }
    }

    public class CombinedAction : Action {
        public List<Action> Actions { get; set; }

        public override void Execute(State S) {
            BeforeLog();
            foreach (var x in Actions)
            {
                x.Execute(S);
                if (x.ActiveAfterExecution) {
                    ActiveAfterExecution = true;
                    x.ActiveAfterExecution = false;
                }
            }
            AfterLog();
        }

        protected bool? long_running;

        public override void Initialize()
        {
            foreach (var x in Actions)
            {
                x.Initialize();
            }
        }

        public override bool LongRunning {
            get {
                if (long_running.HasValue) return long_running.Value;
                long_running = false;
                foreach (var a in Actions) {
                    if (a.LongRunning) {
                        long_running = true;
                        break;
                    }
                }

                return long_running.Value;
            }
        }

        public CombinedAction(IEnumerable<Action> Actions) {
            this.Actions = new List<Action>(Actions);
        }
    }

    public class OneOf : CombinedAction {
        public OneOf(IEnumerable<Action> Actions) : base(Actions) { }

        public override void Execute(State S) {
            BeforeLog();
            var x = Actions.OneOf();
            x.Execute(S);
            if (x.ActiveAfterExecution){
                ActiveAfterExecution = true;
                x.ActiveAfterExecution = false;
            }
            AfterLog();
        }
    }

    public class Say : Action {
        public static UWPLocalSpeaker Speaker { get; set; }
        public int Probability { get; set; }
        public string Text { get; set; }

        public static bool isPlaying = false;

        public Say(string Text, int Probability) {
            this.Text = Text;
            this.Probability = Probability;
        }

        public override void Initialize() {}
        public override bool LongRunning => true;

        public override void Execute(State S)
        {
            BeforeLog();
            var rand = new Random();
            if (rand.Next(1, 101) > Probability)
            {
                return;
            }


            var textToSay = S.EvalString(Text);
            SayHelper(textToSay, S);
            AfterLog();
        }

        public static Say Parse(XElement X) {
            return new Say(X.Attribute("Text").Value, 100);
        }
        public async void SayHelper(String Text, State S)
        {
            BeforeLog("SayHelper", $"Text='{Text}'");
            while (isPlaying)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            isPlaying = true;
            S.Assign("isPlaying", "True");
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunTaskAsync(() => Speaker.Speak(Text));
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            while (Speaker.Media.CurrentState != MediaElementState.Closed && Speaker.Media.CurrentState != MediaElementState.Stopped && Speaker.Media.CurrentState != MediaElementState.Paused) {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
            isPlaying = false;
            S.Assign("isPlaying", "False");
            AfterLog("SayHelper", $"Text='{Text}'");
        }

    }


    public class SayFast : Action
    {
        public static UWPLocalSpeaker Speaker { get; set; }
        public int Probability { get; set; }
        public string Text { get; set; }

        public static bool isPlaying = false;

        private List<Tuple<string, SpeechSynthesisStream>> sentencesPossible = new List<Tuple<string, SpeechSynthesisStream>>();

        public SayFast(string Text, int Probability)
        {
            this.Text = Text;
            this.Probability = Probability;
        }

        public override void Initialize() {
            var rx = new Regex(@"{(.*?)}");
            MatchCollection ms = rx.Matches(this.Text);

            List<int> tmp = new List<int>();
            int lastPos = 0;
            List<string> txt = new List<string>();
            List<string[]> matches = new List<string[]>();
            foreach (Match m in ms)
            {
                txt.Add(this.Text.Substring(lastPos, m.Index - lastPos));
                lastPos = m.Index + m.Length;
                matches.Add(m.Value.Trim('{', '}').Split('|'));
            }
            txt.Add(this.Text.Substring(lastPos, this.Text.Length - lastPos));
            List<string> stringsToSay = new List<string>();
            stringsToSay.Add("");
            for (var i = 0; i < matches.Count; i++)
            {
                string[] ss = matches[i];
                List<string> tmpsts = new List<string>();

                foreach (string t in stringsToSay)
                {
                    foreach (string s in ss)
                    {
                        tmpsts.Add(t + txt[i] + s);
                    }
                    
                }

                stringsToSay = tmpsts;
            }

            for (var i = 0; i < stringsToSay.Count; i++)
            {
                stringsToSay[i] = stringsToSay[i] + txt.Last();
            }
            foreach (var stringToSay in stringsToSay)
            {
                //Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunTaskAsync(async () => {
                Task.Run(async () =>
                {
                    SpeechSynthesisStream speechSynthesisStream = await Speaker.Synthesizer.SynthesizeTextToStreamAsync(stringToSay);
                    sentencesPossible.Add(new Tuple<string, SpeechSynthesisStream>(stringToSay, speechSynthesisStream));
                }).Wait();
            }
        }
        public override bool LongRunning => true;

        public override void Execute(State S)
        {
            BeforeLog();
            var rand = new Random();
            if (rand.Next(1, 101) > Probability)
            {
                return;
            }


            ;
            SayFastHelper(sentencesPossible.OneOf(), S);
            AfterLog();
        }
        
        public static SayFast Parse(XElement X)
        {
            return new SayFast(X.Attribute("Text").Value, 100);
        }
        public async void SayFastHelper(Tuple<string, SpeechSynthesisStream> speechSynthesisStreamTuple, State S)
        {
            BeforeLog("SayFastHelper", $"Text='{speechSynthesisStreamTuple.Item1}'");
            while (isPlaying)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            isPlaying = true;
            S.Assign("isPlaying", "True");
            var u = await ApplicationData.Current.LocalFolder.GetFileAsync(decode(speechSynthesisStreamTuple.Item1) + ".wav.loud");
            
            
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunTaskAsync(() => Speaker.PlayFileAsync(new Uri("ms-appdata:///local/" + decode(speechSynthesisStreamTuple.Item1) + ".wav.loud")));
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            while (Speaker.Media.CurrentState != MediaElementState.Closed && Speaker.Media.CurrentState != MediaElementState.Stopped && Speaker.Media.CurrentState != MediaElementState.Paused)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
            isPlaying = false;
            S.Assign("isPlaying", "False");
            AfterLog("SayFastHelper", $"Text='{speechSynthesisStreamTuple.Item1}'");
        }

        
    }

    public class CompareAnswers : Action {
        public string correctAnswersVarName;
        public string realAnswersVarName;

        public CompareAnswers(string correctAnswersVarName, string realAnswersVarName) {
            this.correctAnswersVarName = correctAnswersVarName;
            this.realAnswersVarName = realAnswersVarName;
        }

        public override void Initialize() { }


        public override void Execute(State S)
        {
            BeforeLog();
            if (S.ContainsKey(correctAnswersVarName) && S.ContainsKey(realAnswersVarName)) {
                var tmp1 = S[correctAnswersVarName];
                var tmp2 = S[realAnswersVarName];
                if (tmp1.Length != tmp2.Length) {
                    S["comparisonRes"] = "error";
                    S["comparisonErrors"] = "error";
                    return ;
                }

                int numOfQuestions = tmp1.Length;
                int numOfGoodQuestions = 0;
                List<int> badQuestions = new List<int>();
                for (int i = 0; i < numOfQuestions; i++)
                {
                    if (tmp1[i] == tmp2[i])
                    {
                        numOfGoodQuestions += 1;
                    }
                    else {
                        badQuestions.Add(i+1);
                    }
                }

                string res = ((int) ((float) numOfGoodQuestions / numOfQuestions * 100)).ToString();
                string errors = string.Join(", ", badQuestions.Select(x => x.ToString()).ToArray());
                S["comparisonRes"] = res;
                S["comparisonErrors"] = errors;
            }
            AfterLog();
        }


    }

    public class ShutUp : Action {
        public static UWPLocalSpeaker Speaker { get; set; }
        public override void Execute(State S) {
            BeforeLog();
            Speaker.ShutUp();
            AfterLog();
        }

        public override void Initialize() { }


        public static ShutUp Parse(XElement X) {
            return new ShutUp();
        }
    }

    public class Play : Action
    {
        private const string WAV_PATH_PREFIX = "ms-appx:///Sounds/";
        public static UWPLocalSpeaker Speaker { get; set; }
        public int Probability { get; set; }
        public Uri FileName { get; set; }//TODO type
        private readonly int _duration = -1;
        public Play(string filename, int prob)
        {
            this.FileName = new Uri(WAV_PATH_PREFIX + filename);
            Probability = prob;
        }

        public override void Initialize() { }

        public Play(string filename, int prob, int duration) {
            FileName = new Uri(WAV_PATH_PREFIX + filename);
            Probability = prob;
            _duration = duration;
        }

        public override bool LongRunning => true;


        public override void Execute(State S)
        {
            BeforeLog();
            var rand = new Random();
            if (rand.Next(1, 101) > Probability)
            {
                return;
            }

            if (_duration == -1) {
                Speaker.Play(FileName);
            }
            else {
                Speaker.Play(FileName, _duration);
            }
            AfterLog();
        }

        public static Play Parse(XElement X, int _prob) {
            return new Play(X.Attribute("FileName").Value, _prob);
        }

    }

    public class StayActive : Action
    {
        public override void Execute(State S) {
            BeforeLog();
            ActiveAfterExecution = true;
            AfterLog();
        }
        public override void Initialize() { }

    }

    public class Extension : Action
    {
        public static Action<string,string> Executor { get; set; }

        public string Command { get; set; }
        public string Param { get; set; }

        public Extension(string Cmd, string Param = null) {
            this.Command = Cmd;
            this.Param = Param;
        }

        public override void Execute(State S) {
            BeforeLog();
            Executor(Command, Param);
            AfterLog();
        }

        public override void Initialize() { }


        public static Extension Parse(XElement X) {
            if (X.Attribute("Param") == null) return new Extension(X.Attribute("Command").Value);
            else return new Extension(X.Attribute("Command").Value, X.Attribute("Param").Value);
        }
    }

    public class GPIO : Action {
        public List<int> Signal { get; set; }
        public int Time;
        public int Probability { get; set; }
        private int[] pinsNums = Config.OutputPinsNumbers;

        CancellationTokenSource tokenSource2;
        CancellationToken ct;
        private Task task = Task.CompletedTask;
        private static Boolean stopExecution = false;
        private static Boolean executing = false;
        public override bool LongRunning => true;
        private Boolean yesNoRequest = false;
        private Boolean yesNoCancel = false;
        public static long yesNoLastStartTime = 0L;
        public static Boolean inYesNo = false;
        public override void Initialize() { }


        public GPIO(IEnumerable<int> signal, int time, int probability) {
            this.Signal = new List<int>(signal);
            if (this.Signal[0] == 1 &&
                this.Signal[1] == 0 &&
                this.Signal[2] == 1 &&
                this.Signal[3] == 1) {
                yesNoRequest = true;
            }
            if (this.Signal[0] == 0 &&
                this.Signal[1] == 1 &&
                this.Signal[2] == 0 &&
                this.Signal[3] == 0)
            {
                yesNoCancel = true;
            }
            this.Time = time;
            this.Probability = probability;
            tokenSource2 = new CancellationTokenSource();
            ct =  tokenSource2.Token;
        }

        public override void Execute(State S)
        {
            BeforeLog();
            var rand = new Random();
            int tmp = rand.Next(1, 101);
            if (tmp > Probability)
            {
                return;
            }
            var gpio = GpioController.GetDefault();
            if (gpio == null) {
                return;
            }

            if (executing) {
                stopExecution = true;
                while (executing) {}
            }

            stopExecution = false;
            executing = true;

            List<GpioPin> pins = new List<GpioPin>();
            //GpioPin pin;
            foreach (var num in pinsNums) {
                var pin = gpio.OpenPin(num);
                pin.SetDriveMode(GpioPinDriveMode.Output);
                pin.Write(GpioPinValue.Low);
                pins.Add(pin);
            }
            
            long startTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (yesNoRequest) {
                yesNoLastStartTime = startTime;
                inYesNo = true;
            }

            if (yesNoCancel) {
                yesNoLastStartTime = 0L;
                inYesNo = false;
            }
            

            string debug = "";
            for (int i = 0; i < 4; ++i) {
                if (Signal[i] == 1)
                    pins[i].Write(GpioPinValue.High);
                debug += pins[i].Read().ToString();
            }
            LogLib.Log.Trace($"Sended {debug}");

            Task.Run(() => {
                while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - startTime < Time) {
                    if (stopExecution) break;
                }

                foreach (var pin in pins) {
                    pin.Write(GpioPinValue.Low);
                    pin.Dispose();
                }
                LogLib.Log.Trace("Disposed");
                executing = false;
            });
            LogLib.Log.Trace("Exited GPIO");
            AfterLog();
        }

        public static GPIO Parse(XElement X) {
            try {
                return new GPIO(
                    X.Attribute("Signal").Value.Split(',', ' ').Select(Int32.Parse).ToList(),
                    Int32.Parse(X.Attribute("Time").Value), 100);
            }
            catch {
                throw new RuleEngineException("Error converting string to number");
            }
        }
    }

    public class Quiz : Action {

        private Uri quizFileName = null;
        public bool randomOrdered = false;
        public int lengthLowerBound;
        public int lengthUpperBound;
        public static UWPLocalSpeaker Speaker;
        private IList<Tuple<string, bool?, string>> _quizText = new List<Tuple<string, bool?, string>>();
        private readonly IList<Tuple<string, bool?, string>> _quiz = new List<Tuple<string, bool?, string>>();
        private static IEnumerable<int> QUESTION_SIGNAL = new []{1, 0, 1, 1};
        private static IEnumerable<int> DEFAULT_SIGNAL = new[] { 0, 1, 0, 0 };

        private static string ARDUINO_NO = "0010";
        private static string ARDUINO_YES = "0100";
        private static string ARDUINO_NONE = "0001";

        private static int QUIZ_QUESTION_TIME_MILLIS = 2000000;
        private GPIO _questionner = new GPIO(QUESTION_SIGNAL, QUIZ_QUESTION_TIME_MILLIS, 100);
        private GPIO _defaultArduinoState = new GPIO(DEFAULT_SIGNAL, 5000, 100);
        public override bool LongRunning => false;
        private Random random = new Random();

        private IRandomAccessStreamWithContentType allOk;
        private IRandomAccessStreamWithContentType youErrored;

        public Quiz(string quizFileName)
        {
            this.quizFileName = new Uri("ms-appx:///Quizs/" + quizFileName);

            Task.Run(async () => {
                var f = await StorageFile.GetFileFromApplicationUriAsync(this.quizFileName);
                using (var inputStream = await f.OpenReadAsync())
                using (var classicStream = inputStream.AsStreamForRead())
                using (TextReader fileReader = new StreamReader(classicStream))
                {
                    var csv = new CsvReader(fileReader);
                    csv.Configuration.HasHeaderRecord = false;
                    csv.Configuration.Delimiter = ";";
                    csv.Configuration.IgnoreQuotes = false;

                    while (csv.Read()) {
                        bool? answ;
                        if (csv.GetField(1) == "да") {
                            answ = true;
                        }
                        else if (csv.GetField(1) == "нет") {
                            answ = false;
                        }
                        else {
                            answ = null;
                        }

                        _quizText.Add(new Tuple<string, bool?, string>(csv.GetField(0), answ, csv.GetField(2)));
                    }
                }
            }).Wait();

            lengthLowerBound = lengthUpperBound = _quizText.Count();
        }
        

        public override void Initialize() {
            Task.Run(async () => {

                //var youMadeErrorsListen = await Speaker.Synthesizer.SynthesizeTextToStreamAsync($"ты допускал ошибки. внимай.");
                //using (var reader = new DataReader(youMadeErrorsListen))
                //{
                //    await reader.LoadAsync((uint)youMadeErrorsListen.Size);
                //    IBuffer buffer = reader.ReadBuffer((uint)youMadeErrorsListen.Size);
                //    var desiredName1 = "quiz_" + decode("ты допускал ошибки. внимай.") + ".wav";
                //    var f = await ApplicationData.Current.LocalFolder.CreateFileAsync(desiredName1, CreationCollisionOption.ReplaceExisting);
                //    await FileIO.WriteBufferAsync(f, buffer);
                //}
                //var u1 = await ApplicationData.Current.LocalFolder.GetFileAsync("quiz_" + decode("ты допускал ошибки. внимай.") + ".wav");
                //youErrored = await u1.OpenReadAsync();


                //var youDidNotMadeErrors = await Speaker.Synthesizer.SynthesizeTextToStreamAsync("всё правильно");
                //using (var reader = new DataReader(youDidNotMadeErrors))
                //{
                //    await reader.LoadAsync((uint)youDidNotMadeErrors.Size);
                //    IBuffer buffer = reader.ReadBuffer((uint)youDidNotMadeErrors.Size);
                //    var desiredName1 = "quiz_" + decode("всё правильно") + ".wav";
                //    var f = await ApplicationData.Current.LocalFolder.CreateFileAsync(desiredName1, CreationCollisionOption.ReplaceExisting);
                //    await FileIO.WriteBufferAsync(f, buffer);
                //}
                //var u2 = await ApplicationData.Current.LocalFolder.GetFileAsync("quiz_" + decode("всё правильно") + ".wav");
                //allOk = await u2.OpenReadAsync();

                for (var i = 0; i < _quizText.Count; i++) {
                    //var speechSynthesisStream1 = await Speaker.Synthesizer.SynthesizeTextToStreamAsync(_quizText.ElementAt(i).Item1);
                    //var speechSynthesisStream3 = await Speaker.Synthesizer.SynthesizeTextToStreamAsync(_quizText.ElementAt(i).Item3);
                    
                    //using (var reader = new DataReader(speechSynthesisStream1))
                    //{
                    //    await reader.LoadAsync((uint)speechSynthesisStream1.Size);
                    //    IBuffer buffer = reader.ReadBuffer((uint)speechSynthesisStream1.Size);
                    //    var desiredName1 = "quiz_" + decode(_quizText.ElementAt(i).Item1) + ".wav";
                    //    var f = await ApplicationData.Current.LocalFolder.CreateFileAsync(desiredName1, CreationCollisionOption.ReplaceExisting);
                    //    await FileIO.WriteBufferAsync(f, buffer);
                    //}
                    //using (var reader = new DataReader(speechSynthesisStream3))
                    //{
                    //    await reader.LoadAsync((uint)speechSynthesisStream3.Size);
                    //    IBuffer buffer = reader.ReadBuffer((uint)speechSynthesisStream3.Size);
                    //    var desiredName3 = "quiz_" + decode(_quizText.ElementAt(i).Item3) + ".wav";
                    //    var f = await ApplicationData.Current.LocalFolder.CreateFileAsync(desiredName3, CreationCollisionOption.ReplaceExisting);
                    //    await FileIO.WriteBufferAsync(f, buffer);
                    //}

                    //var u21 = await ApplicationData.Current.LocalFolder.GetFileAsync("quiz_" + decode(_quizText.ElementAt(i).Item1) + ".wav");
                    //var s21 = await u21.OpenReadAsync();

                    //var u22 = await ApplicationData.Current.LocalFolder.GetFileAsync("quiz_" + decode(_quizText.ElementAt(i).Item3) + ".wav");
                    //var s22 = await u22.OpenReadAsync();

                    _quiz.Add(new Tuple<string, bool?, string>("ms-appdata:///local/" + "quiz_" + decode(_quizText.ElementAt(i).Item1) + ".wav.loud",
                        _quizText.ElementAt(i).Item2,
                        "ms-appdata:///local/" + "quiz_" + decode(_quizText.ElementAt(i).Item3) + ".wav.loud"));
                }
            }).Wait();
        }


        public override void Execute(State S) {
            BeforeLog();
            if (S.ContainsKey("inQuiz"))
            {
                if (S["inQuiz"] == "True")
                {
                    return;
                }
            }
            S.Assign("inQuiz", "True");
            Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunTaskAsync(() => (QuizHelper(S)));
            AfterLog();
        }

        private async Task QuizHelper(State S)
        {
            

            if (!S.ContainsKey("ArduinoInput")) {
                S["ArduinoInput"] = "";
            }
            if (!S.ContainsKey("KeyboardIn"))
            {
                S["KeyboardIn"] = "";
            }
            if (!S.ContainsKey("isPaused"))
            {
                S["isPaused"] = "";
            }
            if (!S.ContainsKey("stopQuiz"))
            {
                S["stopQuiz"] = "";
            }


            var userAnswers = new List<bool?>();
            var correctAnswers = new List<bool?>();

            var length = random.Next(lengthLowerBound, lengthUpperBound);
            var quests = Enumerable.Range(0, _quiz.Count).ToList();
            var questionsToAsk = (randomOrdered ? quests.OrderBy(item => random.Next()).ToList() : quests).GetRange(0, Math.Min(length, _quiz.Count));

            for (var i = 0; i < questionsToAsk.Count; i++) {

                if (S["stopQuiz"] == "True")
                {
                    S.Assign("inQuiz", "False");
                    return;
                }
                if (S["isPaused"] == "True") {
                    await Task.Delay(TimeSpan.FromMilliseconds(300));
                    i--;
                    Log.Trace("paused, waiting");
                    continue;
                }

                
                Log.Trace("continuing");

                while (SayFast.isPlaying) {
                    await Task.Delay(TimeSpan.FromMilliseconds(300));
                }
                var questionToAskPosition = questionsToAsk[i];

                await SpeakingFunction(S, _quiz.ElementAt(questionToAskPosition).Item1);

                try {
                    //bool isPaused = false;
                    _questionner.Execute(S);
                    LogLib.Log.Trace("Started fixation");
                    while (S["ArduinoInput"] != ARDUINO_YES &&
                           S["ArduinoInput"] != ARDUINO_NO &&
                           S["ArduinoInput"] != ARDUINO_NONE) {
                        await Task.Delay(TimeSpan.FromMilliseconds(200));
                        if (S["isPaused"] == "True" || S["stopQuiz"] == "True") {
                            break;
                        }
                    }

                    if (S["stopQuiz"] == "True")
                    {
                        S.Assign("inQuiz", "False");
                        return;
                    }
                    if (S["isPaused"] == "True") {
                        i--;
                        Log.Trace("paused, waiting");
                        continue;
                    }
                    

                    correctAnswers.Add(_quiz.ElementAt(questionToAskPosition).Item2);

                    if ( S["ArduinoInput"] == ARDUINO_NONE) {
                        await Task.Delay(TimeSpan.FromMilliseconds(3000));
                    }


                    if (S["ArduinoInput"] == ARDUINO_YES) {
                        userAnswers.Add(true);
                        S["lastAnswer"] = "True";
                    }
                    else if (S["ArduinoInput"] == ARDUINO_NO) {
                        userAnswers.Add(false);
                        S["lastAnswer"] = "False";
                    }
                    else if (S["ArduinoInput"] == ARDUINO_NONE) {
                        userAnswers.Add(null);
                        S["lastAnswer"] = "None";
                    }

                    LogLib.Log.Trace("Finished fixation");
                    _defaultArduinoState.Execute(S);
                }
                catch (KeyNotFoundException) {
                    S.Assign("inQuiz", "False");
                    return;
                }
            }

            var result = CompareLists(correctAnswers, userAnswers);

            if (S.ContainsKey("sayGood")) {
                if (S["sayGood"] == "True") {
                    if (result.Item1.Count > 0)
                    {
                        //var youMadeErrorsListen = await Speaker.Synthesizer.SynthesizeTextToStreamAsync($"ты допускал ошибки. внимай.");
                        await SpeakingFunction(S, "ms-appdata:///local/" + "quiz_" + decode("ты допускал ошибки. внимай.") + ".wav.loud");
                        foreach (var i in result.Item1)
                        {
                            await SpeakingFunction(S, _quiz.ElementAt(questionsToAsk[i]).Item3);
                        }
                    }
                    else if (result.Item1.Count == 0) {
                        //var youDidNotMadeErrors = await Speaker.Synthesizer.SynthesizeTextToStreamAsync("всё правильно");
                        await SpeakingFunction(S, "ms-appdata:///local/" + "quiz_" + decode("всё правильно") + ".wav.loud");
                    }
                }
            }
            

            S.Assign("inQuiz", "False");

            async Task SpeakingFunction(State state, string s) {
                Say.isPlaying = true;
                SayFast.isPlaying = true;
                state.Assign("isPlaying", "True");
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunTaskAsync(() =>
                    Say.Speaker.PlayFileAsync(new Uri(s)));
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                while (Say.Speaker.Media.CurrentState != MediaElementState.Closed &&
                       Say.Speaker.Media.CurrentState != MediaElementState.Stopped &&
                       Say.Speaker.Media.CurrentState != MediaElementState.Paused) {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }
                state.Assign("isPlaying", "False");

                Say.isPlaying = false;
                SayFast.isPlaying = false;
            }
        }

        private Tuple<List<int>, int> CompareLists(List<bool?> orig, List<bool?> copy) {
            var errors = new List<int>();
            var numberOfCorrectAnswers = 0;
            var numberOfQuestions = orig.Count;

            for (var i = 0; i < numberOfQuestions; i++) {
                if (orig[i] == null) {
                    if (copy[i] != null) {
                        numberOfCorrectAnswers += 1;
                    }
                    else {
                        errors.Add(i);
                    }
                }
                else if (orig[i] == copy[i]) {
                    numberOfCorrectAnswers += 1;
                }
                else {
                    errors.Add(i);
                }
            }

            return new Tuple<List<int>, int>(errors, ((int)((float)numberOfCorrectAnswers / numberOfQuestions * 100)));
        }
    }
}
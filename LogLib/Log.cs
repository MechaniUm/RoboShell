using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Windows.Storage;
using Serilog.Events;

namespace LogLib
{
    public static class Log
    {
        public enum LogFlag
        {
            Debug,
            Information,
            Error
        }

        //public static readonly Logger logger;

        //private static readonly string LogPath = Path.Combine(Environment.ExpandEnvironmentVariables("%userprofile"), "Documents");

        private static readonly string LogPath = ApplicationData.Current.LocalFolder.Path;
        private static Logger logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(LogPath + "\\logs\\Roboshell.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();

        public static void Trace(string s, LogFlag flag = LogFlag.Information)
        {
            switch (flag)
            {
                case LogFlag.Debug:
                    if (logger.IsEnabled(LogEventLevel.Debug)) {
                        logger.Debug(s);
                    }
                    break;
                case LogFlag.Information:
                    if (logger.IsEnabled(LogEventLevel.Information)) {
                        logger.Information(s);
                    }
                    break;
                case LogFlag.Error:
                    if (logger.IsEnabled(LogEventLevel.Information)) {
                        logger.Error(s);
                    }
                    break;
                default:
                    if (logger.IsEnabled(LogEventLevel.Information)) {
                        logger.Error("Bad flag in Trace function");
                    }
                    break;
            }
            System.Diagnostics.Debug.WriteLine(s);
        }
    }
}

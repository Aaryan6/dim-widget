using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DimWidget
{
    internal sealed class Settings
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "DimWidget";

        private static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DimWidget");
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(Folder, "settings.ini"); }
        }

        public int Brightness { get; set; }
        public bool Suspended { get; set; }
        public bool FirstRun { get; set; }
        public bool Expanded { get; set; }
        public int X { get; set; }
        public int Y { get; set; }

        public Settings()
        {
            Brightness = 100;
            Suspended = false;
            FirstRun = true;
            Expanded = false;
            X = int.MinValue;
            Y = int.MinValue;
        }

        public static Settings Load()
        {
            Settings settings = new Settings();

            try
            {
                if (!File.Exists(FilePath))
                {
                    return settings;
                }

                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();

                    switch (key)
                    {
                        case "brightness":
                            int brightness;
                            if (int.TryParse(value, out brightness))
                            {
                                settings.Brightness = Clamp(brightness);
                            }
                            break;

                        case "suspended":
                            settings.Suspended = value == "1";
                            break;

                        case "firstRun":
                            settings.FirstRun = value == "1";
                            break;

                        case "expanded":
                            settings.Expanded = value != "0";
                            break;

                        case "x":
                            int x;
                            if (int.TryParse(value, out x))
                            {
                                settings.X = x;
                            }
                            break;

                        case "y":
                            int y;
                            if (int.TryParse(value, out y))
                            {
                                settings.Y = y;
                            }
                            break;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                List<string> lines = new List<string>
                {
                    "brightness=" + Clamp(Brightness),
                    "suspended=" + (Suspended ? "1" : "0"),
                    "firstRun=" + (FirstRun ? "1" : "0"),
                    "expanded=" + (Expanded ? "1" : "0"),
                    "x=" + X,
                    "y=" + Y
                };
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public bool AutoStart
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    {
                        if (key == null)
                        {
                            return false;
                        }

                        object value = key.GetValue(RunValueName);
                        return value != null;
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    {
                        if (key == null)
                        {
                            return;
                        }

                        if (value)
                        {
                            key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" --startup");
                        }
                        else
                        {
                            key.DeleteValue(RunValueName, false);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        public static int Clamp(int brightness)
        {
            if (brightness < 0)
            {
                return 0;
            }

            if (brightness > 100)
            {
                return 100;
            }

            return brightness;
        }
    }
}

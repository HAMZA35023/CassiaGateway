using System;
using System.IO;
using System.Text;

namespace AccessAppMqttWpf.Services
{
    public static class NotesService
    {
        private const string AppFolderName = "AccessAppMqttWpf";
        private const string AutoFileName = "notes_autosave.txt";

        public static string GetAutoNotesPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, AutoFileName);
        }

        public static string LoadAutoNotes()
        {
            try
            {
                var path = GetAutoNotesPath();
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            }
            catch
            {
                return "";
            }
        }

        public static void SaveAutoNotes(string? text)
        {
            try
            {
                var path = GetAutoNotesPath();
                File.WriteAllText(path, text ?? "", Encoding.UTF8);
            }
            catch
            {
                // ignore autosave errors
            }
        }

        public static string LoadFromFile(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        public static void SaveToFile(string path, string? text)
        {
            File.WriteAllText(path, text ?? "", Encoding.UTF8);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace FlyMod.Inputs
{
    // One optional key per feature, so it can be toggled without opening the
    // menu. Capturing works like the menu key rebind: click the key button,
    // press a key; Esc cancels, Backspace or Delete clears the binding.
    internal class FeatureHotkeys
    {
        public class Binding
        {
            public string Id;
            public string Label;
            public KeyCode Key;
            public KeyCode DefaultKey;
            public Action Toggle;
        }

        public readonly List<Binding> Bindings = new List<Binding>();
        public string ListeningFor { get; private set; }

        private static readonly KeyCode[] AllKeyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        public void Register(string id, string label, KeyCode defaultKey, Action toggle) =>
            Bindings.Add(new Binding { Id = id, Label = label, Key = defaultKey, DefaultKey = defaultKey, Toggle = toggle });

        public Binding Find(string id) => Bindings.Find(binding => binding.Id == id);

        public void ResetToDefaults()
        {
            foreach (Binding binding in Bindings)
                binding.Key = binding.DefaultKey;
            ListeningFor = null;
        }

        public void BeginListening(string id) => ListeningFor = ListeningFor == id ? null : id;

        // Returns true if this frame's input went to a capture.
        public bool CaptureIfListening(KeyCode menuKey)
        {
            if (ListeningFor == null)
                return false;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ListeningFor = null;
                return true;
            }

            Binding binding = Find(ListeningFor);
            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
            {
                if (binding != null)
                    binding.Key = KeyCode.None;
                ListeningFor = null;
                return true;
            }

            foreach (KeyCode key in AllKeyCodes)
            {
                if (key == KeyCode.None || key == menuKey || key.ToString().StartsWith("Mouse") || !Input.GetKeyDown(key))
                    continue;
                if (binding != null)
                {
                    foreach (Binding other in Bindings)
                        if (other != binding && other.Key == key)
                            other.Key = KeyCode.None; // one key, one feature
                    binding.Key = key;
                }
                ListeningFor = null;
                break;
            }
            return true;
        }

        public void Tick(bool blocked)
        {
            if (blocked)
                return;
            foreach (Binding binding in Bindings)
                if (binding.Key != KeyCode.None && Input.GetKeyDown(binding.Key))
                    binding.Toggle();
        }

        public string Encode()
        {
            var parts = new List<string>();
            foreach (Binding binding in Bindings)
                parts.Add(binding.Id + "=" + binding.Key);
            return string.Join(";", parts.ToArray());
        }

        public void Decode(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return;
            foreach (string part in encoded.Split(';'))
            {
                string[] pieces = part.Split('=');
                if (pieces.Length != 2)
                    continue;
                Binding binding = Find(pieces[0]);
                if (binding != null && Enum.TryParse(pieces[1], out KeyCode key))
                    binding.Key = key;
            }
        }

        public static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl: return "Left Ctrl";
                case KeyCode.RightControl: return "Right Ctrl";
                case KeyCode.LeftShift: return "Left Shift";
                case KeyCode.RightShift: return "Right Shift";
                case KeyCode.LeftAlt: return "Left Alt";
                case KeyCode.RightAlt: return "Right Alt";
                case KeyCode.Return: return "Enter";
                default:
                    string name = key.ToString();
                    return name.StartsWith("Alpha") ? name.Substring(5) : name;
            }
        }
    }
}

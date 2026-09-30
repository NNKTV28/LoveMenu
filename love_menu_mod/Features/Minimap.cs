using System;
using UnityEngine;
using FlyMod.Core;
using FlyMod.UI;

namespace FlyMod.Features
{
    // A small map in a screen corner: a top-down picture of the room around
    // you, with dots for you (white arrow), friends (green), your clan
    // (accent), other players (grey) and NPCs (yellow). Hover a dot for its
    // name; click the map to teleport there (a dot, or any spot on the map).
    //
    // The picture comes from a small camera above you looking straight down,
    // redrawn a few times a second at low resolution, starting just above
    // your head so ceilings and roofs don't cover the room. Without it
    // ("Map picture" off) the dots are drawn on a plain background.
    internal class Minimap
    {
        public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

        public static readonly Minimap Instance = new Minimap();
        public static readonly float[] Ranges = { 25f, 50f, 100f, 200f };
        public static readonly string[] RangeNames = { "25 m", "50 m", "100 m", "200 m" };

        public bool Enabled;
        public Corner Position = Corner.TopRight;
        public int RangeIndex = 1;
        public float SizePx = 220f;
        // The map stays north-up; your arrow turns with the camera. (A turning
        // map spilled its corners outside the frame and was hard to read.)
        public const bool TurnWithCamera = false;
        public bool ShowPicture = true;
        public bool ClickToTeleport = true;

        public static bool MouseOver { get; private set; }

        private const float PictureSeconds = 0.33f;
        private const int PictureResolution = 256;
        private const float AboveHead = 3f;

        private Camera _camera;
        private RenderTexture _picture;
        private float _nextPicture;
        private Rect _rect;
        private Vector3 _pictureCenter;
        private Func<Vector3, bool> _teleport;

        public float Range => Ranges[Mathf.Clamp(RangeIndex, 0, Ranges.Length - 1)];

        public void SetTeleport(Func<Vector3, bool> teleport) => _teleport = teleport;

        public void Tick(PlayerContext player)
        {
            if (!Enabled)
            {
                MouseOver = false;
                ReleasePicture();
                return;
            }
            RoomScan.Instance.Want();
            if (!ShowPicture || player.Avatar == null || Time.unscaledTime < _nextPicture)
                return;
            _nextPicture = Time.unscaledTime + PictureSeconds;
            try
            {
                RenderPicture(player.Avatar.transform.position);
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Minimap picture failed: " + exception.Message);
                ShowPicture = false;
            }
        }

        private void RenderPicture(Vector3 center)
        {
            Camera main = Camera.main;
            if (main == null)
                return;
            if (_picture == null)
            {
                _picture = new RenderTexture(PictureResolution, PictureResolution, 16) { name = "LoveMenuMinimap" };
                var holder = new GameObject("LoveMenuMinimapCamera") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(holder);
                _camera = holder.AddComponent<Camera>();
                _camera.enabled = false;
                _camera.orthographic = true;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.06f, 0.05f, 0.09f, 1f);
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                _camera.useOcclusionCulling = false;
                _camera.targetTexture = _picture;
            }
            // Avatars are drawn as dots, not in the picture.
            int skin = LayerMask.NameToLayer("Skin");
            _camera.cullingMask = main.cullingMask & ~(skin >= 0 ? 1 << skin : 0);
            // Big enough that turning the picture never shows its corners.
            _camera.orthographicSize = Range * 1.42f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 80f;
            _camera.transform.position = center + Vector3.up * AboveHead;
            _camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            ShadowQuality shadows = QualitySettings.shadows;
            QualitySettings.shadows = ShadowQuality.Disable;
            try
            {
                _camera.Render();
            }
            finally
            {
                QualitySettings.shadows = shadows;
            }
            _pictureCenter = center;
        }

        private void ReleasePicture()
        {
            if (_camera != null)
                UnityEngine.Object.Destroy(_camera.gameObject);
            if (_picture != null)
            {
                _picture.Release();
                UnityEngine.Object.Destroy(_picture);
            }
            _camera = null;
            _picture = null;
        }

        public void Draw(MenuStyles s, PlayerContext player)
        {
            if (!Enabled || player.Avatar == null || Camera.main == null)
            {
                MouseOver = false;
                return;
            }
            float size = s.S(SizePx);
            float margin = s.S(16);
            float x = Position == Corner.TopLeft || Position == Corner.BottomLeft ? margin : Screen.width - size - margin;
            float y = Position == Corner.TopLeft || Position == Corner.TopRight ? s.S(110) : Screen.height - size - s.S(70);
            _rect = new Rect(x, y, size, size);
            Vector2 mouse = Event.current.mousePosition;
            MouseOver = _rect.Contains(mouse);

            Vector3 me = player.Avatar.transform.position;
            float yaw = TurnWithCamera ? Camera.main.transform.eulerAngles.y : 0f;
            float scale = size / 2f / Range;
            Vector2 center = _rect.center;

            GUI.Box(_rect, GUIContent.none, s.Card);
            GUI.BeginGroup(_rect);
            Vector2 local = new Vector2(size / 2f, size / 2f);
            if (ShowPicture && _picture != null)
            {
                // The picture was taken around _pictureCenter; shift it by how
                // far you've moved since, then turn it with the camera.
                Vector3 moved = me - _pictureCenter;
                float pictureSize = size * 1.42f;
                Matrix4x4 before = GUI.matrix;
                GUIUtility.RotateAroundPivot(-yaw, local);
                var pictureRect = new Rect(local.x - pictureSize / 2f - moved.x * scale, local.y - pictureSize / 2f + moved.z * scale, pictureSize, pictureSize);
                Color tint = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.9f);
                GUI.DrawTexture(pictureRect, _picture, ScaleMode.StretchToFill, false);
                GUI.color = tint;
                GUI.matrix = before;
            }

            // Dots.
            RoomScan.Entry hover = null;
            float hoverDistance = s.S(9);
            foreach (RoomScan.Entry entry in RoomScan.Instance.Entries)
            {
                if (entry.Kind == RoomScan.Kind.Me)
                    continue;
                Vector2 point = ToMap(entry.Position - me, yaw, scale, local);
                if (point.x < 2 || point.y < 2 || point.x > size - 2 || point.y > size - 2)
                    continue;
                Color color = ColorOf(entry.Kind);
                float dot = entry.Kind == RoomScan.Kind.Friend || entry.Kind == RoomScan.Kind.Clan ? s.S(7) : s.S(5);
                Widgets.FillCircle(s, new Rect(point.x - dot / 2f, point.y - dot / 2f, dot, dot), color);
                float distance = Vector2.Distance(point, mouse - _rect.position);
                if (MouseOver && distance < hoverDistance)
                {
                    hoverDistance = distance;
                    hover = entry;
                }
            }

            // You: an arrow pointing where the camera looks.
            float myYaw = TurnWithCamera ? 0f : Camera.main.transform.eulerAngles.y;
            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(myYaw, local);
            float arrow = s.S(12);
            Widgets.DrawIcon(new Rect(local.x - arrow / 2f, local.y - arrow / 2f, arrow, arrow), Icons.Arrow, Color.white);
            GUI.matrix = saved;
            GUI.EndGroup();

            // Range label and hover name.
            GUI.Label(new Rect(_rect.x + s.S(8), _rect.yMax - s.S(22), size, s.S(18)), RangeNames[RangeIndex], s.Small);
            if (hover != null)
            {
                string label = hover.Name + (hover.Clan.Length > 0 ? " · " + hover.Clan : "") + " · " +
                    Vector3.Distance(hover.Position, me).ToString("0") + " m";
                var content = new GUIContent(label);
                Vector2 labelSize = s.Small.CalcSize(content);
                var labelRect = new Rect(mouse.x + s.S(12), mouse.y - s.S(6), labelSize.x + s.S(12), labelSize.y + s.S(6));
                GUI.Box(labelRect, GUIContent.none, s.Card);
                GUI.Label(new Rect(labelRect.x + s.S(6), labelRect.y + s.S(3), labelSize.x, labelSize.y), content, s.Small);
            }

            // Click to teleport: to the dot under the mouse, or to that spot.
            Event e = Event.current;
            if (ClickToTeleport && MouseOver && e.type == EventType.MouseDown && e.button == 0 && _teleport != null)
            {
                Vector3 target = hover != null ? hover.Position + Vector3.back * 1.5f : FromMap(mouse - _rect.position, yaw, scale, local) + me;
                if (_teleport(target))
                    Toasts.Show(hover != null ? "Teleported to " + hover.Name : "Teleported");
                e.Use();
            }
        }

        private static Color ColorOf(RoomScan.Kind kind)
        {
            switch (kind)
            {
                case RoomScan.Kind.Friend: return new Color(0.29f, 0.87f, 0.5f);
                case RoomScan.Kind.Clan: return Theme.AccentText;
                case RoomScan.Kind.Npc: return new Color(1f, 0.8f, 0.25f);
                default: return new Color(0.75f, 0.75f, 0.8f);
            }
        }

        // World offset -> point in the map (map up = north, or camera forward).
        private static Vector2 ToMap(Vector3 offset, float yawDegrees, float scale, Vector2 center)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float right = offset.x * Mathf.Cos(yaw) - offset.z * Mathf.Sin(yaw);
            float up = offset.x * Mathf.Sin(yaw) + offset.z * Mathf.Cos(yaw);
            return new Vector2(center.x + right * scale, center.y - up * scale);
        }

        private static Vector3 FromMap(Vector2 point, float yawDegrees, float scale, Vector2 center)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float right = (point.x - center.x) / scale;
            float up = (center.y - point.y) / scale;
            float x = right * Mathf.Cos(yaw) + up * Mathf.Sin(yaw);
            float z = -right * Mathf.Sin(yaw) + up * Mathf.Cos(yaw);
            return new Vector3(x, 0f, z);
        }
    }
}

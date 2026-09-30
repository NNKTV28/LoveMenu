using UnityEngine;
using FlyMod.Core;
using FlyMod.UI;

namespace FlyMod.Features
{
    // Auto crowd mode: when a room gets packed and the frame rate drops,
    // switch Show people to Friends (your choice of mode is kept); when the
    // crowd thins out, put your choice back. Decided on the head count, not
    // the frame rate, on the way back - otherwise hiding people would raise
    // the FPS and flip it straight back.
    internal class AutoCrowd
    {
        public static readonly AutoCrowd Instance = new AutoCrowd();

        public bool Enabled;
        public int FpsBelow = 40;
        public int CrowdAtLeast = 25;
        public bool Active { get; private set; }

        private const float CheckSeconds = 2f;
        private const float SlowForSeconds = 6f;
        private const int CalmBelowFactorPercent = 60;   // restore when the crowd drops to 60%

        private float _nextCheck, _slowSince = -1f;
        private float _fpsAverage = 60f;
        private PeopleFilter.Mode _before;

        public void Tick()
        {
            float delta = Time.unscaledDeltaTime;
            if (delta > 0f)
                _fpsAverage = Mathf.Lerp(_fpsAverage, 1f / delta, 0.05f);
            if (!Enabled)
            {
                if (Active)
                    Restore();
                return;
            }
            RoomScan.Instance.Want();
            if (Time.unscaledTime < _nextCheck)
                return;
            _nextCheck = Time.unscaledTime + CheckSeconds;

            int crowd = RoomScan.Instance.PlayerCount;
            if (!Active)
            {
                bool slow = _fpsAverage < FpsBelow && crowd >= CrowdAtLeast;
                if (!slow)
                {
                    _slowSince = -1f;
                    return;
                }
                if (_slowSince < 0f)
                    _slowSince = Time.unscaledTime;
                if (Time.unscaledTime - _slowSince < SlowForSeconds)
                    return;
                if (PeopleFilter.Instance.Current != PeopleFilter.Mode.Everyone)
                    return;          // you already chose a filter
                _before = PeopleFilter.Instance.Current;
                PeopleFilter.Instance.Current = PeopleFilter.Mode.Friends;
                Active = true;
                Toasts.Show("Crowd mode on: showing friends only (" + crowd + " people here)");
                return;
            }
            if (crowd < CrowdAtLeast * CalmBelowFactorPercent / 100)
                Restore();
        }

        private void Restore()
        {
            Active = false;
            _slowSince = -1f;
            if (PeopleFilter.Instance.Current == PeopleFilter.Mode.Friends)
                PeopleFilter.Instance.Current = _before;
            Toasts.Show("Crowd mode off: showing everyone again");
        }
    }
}

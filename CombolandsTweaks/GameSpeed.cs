using GameState;
using UnityEngine;

namespace CombolandsTweaks
{
    // Extra speed on top of the game's own Game Speed setting (0.5x-1.75x). TimeManager.GameSpeedMultiplier scales
    // the delays between triggers and the scoring animations; the game sets it from its settings menu.
    internal class GameSpeed : MonoBehaviour
    {
        private float _base = -1f;
        private float _applied = -1f;

        private void Update()
        {
            float current = TimeManager.GameSpeedMultiplier;
            // Anything other than what we last wrote came from the game's settings menu: that's the new base.
            if (_applied < 0f || !Mathf.Approximately(current, _applied))
                _base = current;

            float wanted = _base * Mathf.Max(1f, Plugin.SpeedBoost.Value);
            if (!Mathf.Approximately(current, wanted))
                TimeManager.GameSpeedMultiplier = wanted;
            _applied = wanted;
        }

        private void OnDestroy()
        {
            if (_base > 0f)
                TimeManager.GameSpeedMultiplier = _base;
        }
    }
}

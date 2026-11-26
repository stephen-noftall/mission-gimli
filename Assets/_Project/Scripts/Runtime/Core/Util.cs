using UnityEngine;

namespace MoonBaseAlpha
{
    public static class Util
    {
        /// <summary>Destroy that also works in edit mode (headless verification runs there).</summary>
        public static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o);
        }
    }
}

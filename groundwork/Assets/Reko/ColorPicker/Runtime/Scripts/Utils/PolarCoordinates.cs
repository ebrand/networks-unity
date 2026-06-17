using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Reko.ColorPicker
{
    internal static class PolarCoordinates
    {
        private const float PI2 = Mathf.PI * 2;

        public static Vector2 ConvertFromPolarCoordinates(this Vector2 polarCoordinates)
        {
            var angular = polarCoordinates.GetAngularCoordinate() * PI2;
            var radial = polarCoordinates.GetRadialCoordinate();
            return new Vector2(radial * Mathf.Cos(angular), radial * Mathf.Sin(angular));
        }

        public static Vector2 ConvertToPolarCoordinates(this Vector2 position)
        {
            var a = Mathf.Atan2(position.y, position.x) / PI2;
            if (a < 0) a += 1;
            return new Vector2(position.magnitude, a);
        }

        public static float GetRadialCoordinate(this Vector2 polarCoordinates)
        {
            return polarCoordinates[0];
        }

        public static float GetAngularCoordinate(this Vector2 polarCoordinates)
        {
            return polarCoordinates[1];
        }

        /// <param name="radial">Distance from center</param>
        /// <param name="angular">Angle in the range of [0,1]</param>
        public static Vector2 Create(float radial, float angular)
        {
            return new Vector2(radial, angular);
        }
    }
}

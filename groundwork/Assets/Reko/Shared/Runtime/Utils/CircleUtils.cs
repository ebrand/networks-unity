using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Reko
{
    public static class CircleUtils
    {
        private const int k_minTesselation = 8;
        private const int k_maxTesselation = 50;
        private const int k_segmentSize = 3;
        private const float k_fullCircle = Mathf.PI * 2;


        public static int ComputeTesselation(float radius)
        {
            var perimeter = (2 * radius * Mathf.PI);
            var tesselation = (int)(perimeter / k_segmentSize);
            tesselation = Mathf.Clamp(tesselation, k_minTesselation, k_maxTesselation);
            return tesselation;
        }
        public static Vector2 GetCircleVector(float angle)
        {
            return new Vector3(-Mathf.Cos(angle), Mathf.Sin(angle), 0.0f);
        }

        public static Vector2 GetCirclePosition(float angle, float radius)
        {
            return radius * GetCircleVector(angle);
        }

        public static IEnumerable<Vector2> GetCirclePositions(float radius)
        {
            var tesselation = ComputeTesselation(radius);
            return GetCirclePositions(radius, tesselation);
        }

        public static IEnumerable<Vector2> GetCirclePositions(float radius, int tesselation)
        {
            return GetCirclePositions(radius, 0.0f, k_fullCircle, tesselation);
        }

        public static IEnumerable<Vector2> GetCirclePositions(float radius, float start, float end)
        {
            var tesselation = ComputeTesselation(radius);
            return GetCirclePositions(radius, start, end, tesselation);
        }

        public static IEnumerable<Vector2> GetCirclePositions(float radius, float start, float end, int tesselation)
        {
            var circleSpan = end - start;
            var count = (int)(tesselation * (Mathf.Abs(circleSpan) / k_fullCircle));
            var step = circleSpan / (count);

            for (int i = 0; i < count+1; i++)
            {
                var angle = start + step * i;
                yield return GetCirclePosition(angle, radius);
            }
        }

        public static float GetAngle(Vector2 direction)
        {
            var angle = Mathf.Atan2(direction.y, -direction.x);
            if (angle < 0)
                angle += Mathf.PI * 2;
            return angle;
        }


    }
}


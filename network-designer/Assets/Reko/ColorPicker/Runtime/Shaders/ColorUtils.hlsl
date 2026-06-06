#ifndef COLOR_UTILS_LIB
#define COLOR_UTILS_LIB

static const float PI = 3.14159265f;

#if !UNITY_COLORSPACE_GAMMA
half3 RemoveGammaCorrection(half3 color)
{
	return pow(abs(color.rgb), 0.454545f);
}

half3 ApplyGammaCorrection(half3 color)
{
	return pow(abs(color.rgb), 2.2f);
}
#endif

half3 HueToRGB(float hue)
{
	hue = frac(hue) * 6;

	float r = abs(hue - 3) - 1;
	float g = 2 - abs(hue - 2);
	float b = 2 - abs(hue - 4);
	half3 rgb = half3(r, g, b);
	rgb = saturate(rgb);

	return rgb;
}



half4 HSVToRGB(half4 hsv)
{
	half3 rgb = HueToRGB(hsv.x);
	rgb = lerp(1, rgb, hsv.y);
	rgb = rgb * hsv.z;

#if !UNITY_COLORSPACE_GAMMA
	rgb = ApplyGammaCorrection(rgb);
#endif

	return half4(rgb, hsv.a);
}



half4 RGBToHSV(half4 rgb)
{
#if !UNITY_COLORSPACE_GAMMA
	rgb.rgb = RemoveGammaCorrection(rgb.rgb);
#endif

	float cMax = max(rgb.r, max(rgb.g, rgb.b));
	float cMin = min(rgb.r, min(rgb.g, rgb.b));
	float delta = cMax - cMin;

	half3 diff = ((rgb.gbr - rgb.brg) / delta + half3(0, 2, 4)) / 6.0f;

	half4 hsv = half4(0, 0, cMax, rgb.a);
	if (cMax != 0)
	{
		hsv.g = delta / cMax;
	}

	if (delta == 0)
	{
		hsv.x = 0;
	}
	else if (rgb.r > rgb.g && rgb.r > rgb.b)
	{
		hsv.x = diff.x;
	}
	else if (rgb.g > rgb.b)
	{
		hsv.x = diff.y;
	}
	else
	{
		hsv.x = diff.z;
	}
	if (hsv.x < 0)
		hsv.x += 1.0f;

	return hsv;
}

// pos is defined in the range [-1, 1]
float2 GetPolarCoordinates(float2 pos)
{
	float radial = (atan2(pos.y, pos.x) + PI) / (PI * 2);
	float angular = min(1, length(pos));
	return float2(angular, radial);
}
#endif
Shader "Hidden/HZ/CubemapToEquirect"
{
    Properties
    {
        _Cubemap ("Cubemap", Cube) = "" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            samplerCUBE _Cubemap;

            // Map equirect (u, v) -> direction with engine convention
            // (matches env_equirect_to_cubemap / env_sample_equirect in environment_bake.c):
            //   u = atan2(dir.z, dir.x) / (2*pi) + 0.5
            //   v = asin(dir.y) / pi + 0.5, then v = 1 - v   (top of image = +Y)
            // Engine is RH Y-up; Unity is LH Y-up. Negate X to convert engine RH dir
            // -> Unity LH dir for cubemap sampling.
            float4 frag(v2f_img i) : SV_Target
            {
                float u = i.uv.x;
                // Unity blit: i.uv.y = 0 at bottom. We want top of equirect (+Y) at v = 0.
                float v = 1.0 - i.uv.y;

                float lon = (u - 0.5) * 6.28318530717958647692; // 2*pi
                float lat = (0.5 - v) * 3.14159265358979323846; // pi/2 .. -pi/2

                float cosLat = cos(lat);
                float dxe = cosLat * cos(lon);
                float dye = sin(lat);
                float dze = cosLat * sin(lon);

                // engine RH -> Unity LH: negate X
                float3 dirUnity = float3(-dxe, dye, dze);

                return texCUBE(_Cubemap, dirUnity);
            }
            ENDCG
        }
    }
}

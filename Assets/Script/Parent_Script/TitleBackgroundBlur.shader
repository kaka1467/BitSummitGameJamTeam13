Shader "Parent/TitleBackgroundBlur"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _BlurRadius ("Blur Radius (texels)", Float) = 3
        _Direction ("Direction (x,y)", Vector) = (1,0,0,0)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _BlurRadius;
            float4 _Direction;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // 1方向のガウス（重み 1,4,6,4,1 / 16）。横→縦の2パスで滑らかにぼかす。
            fixed4 frag(v2f i) : SV_Target
            {
                float2 dir = normalize(_Direction.xy) * _MainTex_TexelSize.xy * _BlurRadius;

                fixed4 c  = tex2D(_MainTex, i.uv)              * 6.0;
                c += tex2D(_MainTex, i.uv + dir * 1.0)         * 4.0;
                c += tex2D(_MainTex, i.uv - dir * 1.0)         * 4.0;
                c += tex2D(_MainTex, i.uv + dir * 2.0)         * 1.0;
                c += tex2D(_MainTex, i.uv - dir * 2.0)         * 1.0;

                return c / 16.0;
            }
            ENDCG
        }
    }

    Fallback Off
}

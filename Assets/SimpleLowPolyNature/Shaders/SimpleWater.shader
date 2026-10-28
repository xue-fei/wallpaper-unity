// URP 版水面着色器（由 LowPoly/SimpleWater 的 Built-in Surface Shader 移植）
//
// 为什么必须重写：
//   原版是 `#pragma surface surf StandardSpecular` 的 Built-in 表面着色器，
//   依赖 UnityCG.cginc / UnityStandardUtils.cginc / GrabPass。
//   URP 下这两套 include 与 GrabPass 全部失效 → 没有任何 Pass 能被 URP 识别 →
//   Unity 回退到内置 Error 着色器 → 显示粉红色。
//
// 移植要点（与原版逐项对应，见 git 版本对照）：
//   1. 顶点波动     sin(_WavesAmount * pos.z + _Time.y) * normal * _WavesAmplitude   —— 保留
//   2. 双法线滚动   panner(-0.03,0) + panner(0.04,0.04)，BlendNormals + UnpackScaleNormal —— 保留
//   3. 深度混合     LinearEyeDepth(_CameraDepthTexture) - screenPos.w → pow+falloff → Deep/Shallow 插值 —— 保留
//   4. 泡沫         FoamDepth/FoamFalloff + 滚动 foam 贴图 —— 保留
//   5. 屏幕折射     GrabPass → 改为 URP 的 _CameraOpaqueTexture + 切线空间法线扰动 —— 保留
//   6. 高光         StandardSpecular → URP Lit 的 Specular setup 近似（_SpecColor + _Smoothness）
//
// URP 前提（否则折射/深度仍异常，但不会是粉红）：
//   URP Asset 需勾选 Depth Texture 与 Opaque Texture。
Shader "LowPoly/SimpleWater"
{
    Properties
    {
        [Header(Water Surface)]
        _WaterNormal("Water Normal", 2D) = "bump" {}
        _NormalScale("Normal Scale", Float) = 1
        _DeepColor("Deep Color", Color) = (0.25, 0.72, 0.93, 1)
        _ShalowColor("Shalow Color", Color) = (1, 1, 1, 1)
        _WaterDepth("Water Depth", Float) = 0
        _WaterFalloff("Water Falloff", Float) = -10
        _WaterSpecular("Water Specular", Float) = 0.3
        _WaterSmoothness("Water Smoothness", Float) = 0.3

        [Header(Refraction)]
        _Distortion("Distortion", Float) = 0.5

        [Header(Foam)]
        _Foam("Foam", 2D) = "white" {}
        _FoamDepth("Foam Depth", Float) = 0
        _FoamFalloff("Foam Falloff", Float) = -50
        _FoamSpecular("Foam Specular", Float) = 0.3
        _FoamSmoothness("Foam Smoothness", Float) = 0.3

        [Header(Waves)]
        _WavesAmplitude("WavesAmplitude", Float) = 0.01
        _WavesAmount("WavesAmount", Float) = 8.87

        [HideInInspector] _texcoord("", 2D) = "white" {}
        [HideInInspector] __dirty("", Int) = 1
    }

    SubShader
    {
        // 与原版一致：Transparent 队列 + 不透明 RenderType
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Transparent+0"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            // 阴影接收：不声明这些变体，水面就不会接收阴影（表现是阴影区过曝发亮）
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _LIGHT_COOKIES
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog

            // GPU 实例化
            #pragma multi_compile_instancing

            #pragma vertex vert
            #pragma fragment frag

            // URP 核心库（替代 UnityCG.cginc）
            // Core.hlsl 已包含 Common.hlsl / Packing.hlsl（UnpackNormalScale）/ Input.hlsl（_ZBufferParams）
            // Lighting.hlsl 已包含 GlobalIllumination.hlsl（SampleSH）与 RealtimeLights.hlsl（GetMainLight）
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            // 材质属性必须三 Pass 完全一致（SRP Batcher 要求），且本文件多 Pass 内不可重定义同名变量，
            // 因此统一放在 Core.hlsl 之后、由各 Pass 各自引用。
            CBUFFER_START(UnityPerMaterial)
                float4 _WaterNormal_ST;
                float4 _Foam_ST;
                float  _NormalScale;
                float4 _DeepColor;
                float4 _ShalowColor;
                float  _WaterDepth;
                float  _WaterFalloff;
                float  _WaterSpecular;
                float  _WaterSmoothness;
                float  _Distortion;
                float  _FoamDepth;
                float  _FoamFalloff;
                float  _FoamSpecular;
                float  _FoamSmoothness;
                float  _WavesAmplitude;
                float  _WavesAmount;
            CBUFFER_END

            TEXTURE2D(_WaterNormal);   SAMPLER(sampler_WaterNormal);
            TEXTURE2D(_Foam);          SAMPLER(sampler_Foam);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;   // 手性在 .w，必须是 float4
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float3 tangentWS  : TEXCOORD4;
                float3 bitangentWS: TEXCOORD5;
                half   tangentSign: TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // 对应原版 BlendNormals（UnityStandardUtils.cginc）的 whiteout blend
            float3 BlendNormalsWhiteout(float3 n1, float3 n2)
            {
                return normalize(float3(n1.xy + n2.xy, n1.z * n2.z));
            }

            // 原版 ASE PowerNode 的符号保护语义：
            //   e == 0            -> 1
            //   否则              -> sign(b) * pow(abs(b), e)
            // 因为 pow(负数, 非整数指数) 在 GPU 上是未定义的（返回 NaN），
            // 而底数（深度差 + 深度偏移）在水比地形深时确实会为负。
            // 不做保护 → 水面出现闪烁黑块/破洞。
            float SafePow(float b, float e)
            {
                if (e == 0.0) return 1.0;
                float a = abs(b);
                return (b < 0.0 ? -1.0 : 1.0) * pow(a, e);
            }

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // ---- 1. 顶点波动（与原版 vertexDataFunc 完全一致）----
                float3 positionOS = v.positionOS.xyz;
                float3 normalOS   = v.normalOS.xyz;
                positionOS += (sin((_WavesAmount * positionOS.z) + _Time.y) * normalOS) * _WavesAmplitude;

                VertexPositionInputs posInputs = GetVertexPositionInputs(positionOS);
                o.positionCS  = posInputs.positionCS;
                o.positionWS  = posInputs.positionWS;
                o.uv          = v.uv;

                // 屏幕坐标：与 URP 官方 Shadows.hlsl 的 GetShadowCoord 同一路径
                // （内部含 *0.5 + o.w，并做 UNITY_UV_STARTS_AT_TOP 翻转），
                // 等价于原版 surface shader 的 i.screenPos（也就是 tex2Dproj 的输入）。
                // 注意：不能改用 ComputeNormalizedDeviceCoordinatesWithZ(positionCS)，
                //       该函数内部会丢掉 w（float4(position, 1.0)），透视除法失效。
                o.screenPos = ComputeScreenPos(posInputs.positionCS);

                // 切线空间基向量（供片元做切线空间法线 → 世界空间转换）
                o.normalWS    = TransformObjectToWorldNormal(v.normalOS);
                // unity_WorldTransformParams.w 处理负缩放导致的副切线翻转
                o.tangentWS   = TransformObjectToWorldDir(v.tangentOS.xyz);
                o.bitangentWS = cross(o.normalWS, o.tangentWS) * v.tangentOS.w * unity_WorldTransformParams.w;
                // 当前水面网格（内置 Plane）没有切线数据 → w 为 0，这里做保护
                o.tangentSign = v.tangentOS.w;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // ---- 2. 双法线滚动（与原版一致）----
                float2 uv_WaterNormal = i.uv * _WaterNormal_ST.xy + _WaterNormal_ST.zw;
                float2 panner22 = uv_WaterNormal + _Time.y * float2(-0.03, 0.0);
                float2 panner19 = uv_WaterNormal + _Time.y * float2(0.04, 0.04);

                float3 n1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, panner22), _NormalScale);
                float3 n2 = UnpackNormalScale(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, panner19), _NormalScale);
                // 原版 o.Normal 是切线空间向量，必须乘 TBN 才是世界空间
                float3 normalTS = BlendNormalsWhiteout(n1, n2);
                float3 N = normalize(i.normalWS);
                float3 normalWS;
                if (abs(i.tangentSign) > 0.0001)
                {
                    // 有切线：正确 TBN 变换（副切线带手性符号）
                    float3 T = normalize(i.tangentWS);
                    float3 B = normalize(i.bitangentWS);
                    normalWS = normalize(T * normalTS.x + B * normalTS.y + N * normalTS.z);
                }
                else
                {
                    // 无切线（内置 Plane / 未导入切线的 FBX）：等轴缩放用 world X/Y 做副法线，
                    // 与原贴图 UV 轴对齐，效果等价于自动切线
                    float3 bitangentWS = cross(i.normalWS, float3(1.0, 0.0, 0.0));
                    float3 tangentWS   = cross(bitangentWS, i.normalWS);
                    normalWS = normalize(tangentWS * normalTS.x + bitangentWS * normalTS.y + N * normalTS.z);
                }

                // ---- 3. 深度差（与原版 eyeDepth1 - screenPos.w 一致）----
                // 原版 tex2Dproj 隐含一次除 w，这里显式做同样的事
                float2 screenUV = i.screenPos.xy / i.screenPos.w;
                float  eyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                // 原版 ase_screenPos.w = screenPos.w + 1e-11（防除零），等价于未除的 clip.w
                float  depthDiff = abs(eyeDepth - i.screenPos.w);

                // 水色：深处 _DeepColor → 浅处 _ShalowColor
                // 底数 depthDiff + _WaterDepth 在水比地形深时为负 → 必须走 SafePow
                float  waterFactor = saturate(SafePow(depthDiff + _WaterDepth, _WaterFalloff));
                float4 waterColor  = lerp(_DeepColor, _ShalowColor, waterFactor);

                // ---- 4. 泡沫 ----
                float2 uv_Foam = i.uv * _Foam_ST.xy + _Foam_ST.zw;
                float2 panner116 = uv_Foam + _Time.y * float2(-0.01, 0.01);
                float  foamTex   = SAMPLE_TEXTURE2D(_Foam, sampler_Foam, panner116).r;
                float  foamFactor = saturate(SafePow(depthDiff + _FoamDepth, _FoamFalloff)) * foamTex;
                float4 foamColor  = lerp(waterColor, float4(1, 1, 1, 1), foamFactor);

                // ---- 5. 屏幕折射（GrabPass → _CameraOpaqueTexture）----
                // 原版扰动量是【切线空间】法线 xy × _Distortion，即扰动幅度在 -1..1 之间；
                // 不能改用世界空间法线（世界法线 xy 最大可达 1 但方向任意，折射会整体漂移）。
                float2 distortTS = normalTS.xy * _Distortion;
                half3  sceneColor = SampleSceneColor(screenUV + distortTS);
                float4 finalColor = lerp(foamColor, float4(sceneColor, 1), waterFactor);

                // ---- 6. 高光 / 平滑度（Specular setup 近似）----
                float specular   = lerp(_WaterSpecular, _FoamSpecular, foamFactor);
                float smoothness = lerp(_WaterSmoothness, _FoamSmoothness, foamFactor);

                Light mainLight = GetMainLight();
                float3 V = normalize(_WorldSpaceCameraPos - i.positionWS);
                float3 H = normalize(mainLight.direction + V);
                float  NdotH = saturate(dot(normalWS, H));
                float  specPower = exp2(smoothness * 10.0 + 1.0);
                // 与 URP Lit 一致：环境反射/高光要乘主光源的 shadowAttenuation
                float  shadowAtten = mainLight.shadowAttenuation;
                // NdotH 经 saturate 已保证 ≥ 0，但统一走 SafePow 以消除编译器的负底数警告
                float3 specColor = mainLight.color * specular * SafePow(NdotH, specPower) * shadowAtten;

                float3 albedo  = finalColor.rgb;
                float3 diffuse = albedo * mainLight.color * saturate(dot(normalWS, mainLight.direction));
                float3 ambient = SampleSH(normalWS) * albedo;

                // 环境光同样受主光源阴影影响，否则阴影区的水面会整体发亮
                float3 outColor = (diffuse + ambient + specColor) * shadowAtten;
                return half4(outColor, 1.0);
            }
            ENDHLSL
        }

        // 阴影投射：URP 必须显式提供。
        // 注意：不能直接用 ShadowCasterPass.hlsl —— 它内部依赖 _BaseMap/_BaseColor/_Cutoff，本 shader 没有这些属性。
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma vertex shadowVert
            #pragma fragment shadowFrag

            // 顺序要求：Core.hlsl 必须最先（TransformObjectToWorld / TransformWorldToHClip /
            //          UNITY_REVERSED_Z 等都来自它）。
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Shadows.hlsl 内部用到 LerpWhiteTo，它定义在 CommonMaterial.hlsl，
            // 而 Shadows.hlsl 【自己不 include】它（URP 官方 Lit.shader 是靠
            // LitInput.hlsl → SurfaceInput.hlsl 间接引入才碰巧可用）。
            // 必须显式补上，否则 d3d11 报 "undeclared identifier 'LerpWhiteTo'"。
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            ShadowVaryings shadowVert(ShadowAttributes v)
            {
                ShadowVaryings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(v.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = positionCS;
                return o;
            }

            half4 shadowFrag(ShadowVaryings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return 0;
            }
            ENDHLSL
        }

        // 深度预渲染：URP 生成 _CameraDepthTexture 需要此 Pass。
        // 同样不能用 DepthOnlyPass.hlsl（依赖 _BaseMap / _Cutoff）。
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex depthVert
            #pragma fragment depthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings depthVert(DepthAttributes v)
            {
                DepthVaryings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 depthFrag(DepthVaryings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return 0;
            }
            ENDHLSL
        }
    }

    // URP 下不要 Fallback 到 Built-in 的 Diffuse（会再次导致粉红）
    Fallback Off
    // 注意：原版用 CustomEditor "ASEMaterialInspector"，但本项目未安装 AmplifyShaderEditor，
    // 保留会导致材质 Inspector 报 "Could not find custom editor" 并显示异常。
    // 已移除，改用默认材质面板（所有属性仍可正常编辑）。
}

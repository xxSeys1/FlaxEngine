// File generated by Flax Materials Editor
// Version: @0

#define MATERIAL 1
#define USE_PER_VIEW_CONSTANTS 1
@3

#include "./Flax/Common.hlsl"
#include "./Flax/MaterialCommon.hlsl"
#include "./Flax/GBufferCommon.hlsl"
@7
// Primary constant buffer (with additional material parameters)
META_CB_BEGIN(0, Data)
float4x4 WorldMatrix;
float4x4 InvWorld;
float4x4 SVPositionToWorld;
@1META_CB_END

// Use depth buffer for per-pixel decal layering
Texture2D DepthBuffer : register(t0);

// Material shader resources
@2
// Material properties generation input
struct MaterialInput
{
	float3 WorldPosition;
	float TwoSidedSign;
	float2 TexCoord;
	float3x3 TBN;
	float4 SvPosition;
	float3 PreSkinnedPosition;
	float3 PreSkinnedNormal;
};

// Transforms a vector from tangent space to world space
float3 TransformTangentVectorToWorld(MaterialInput input, float3 tangentVector)
{
	return mul(tangentVector, input.TBN);
}

// Transforms a vector from world space to tangent space
float3 TransformWorldVectorToTangent(MaterialInput input, float3 worldVector)
{
	return mul(input.TBN, worldVector);
}

// Transforms a vector from world space to view space
float3 TransformWorldVectorToView(MaterialInput input, float3 worldVector)
{
	return mul(worldVector, (float3x3)ViewMatrix);
}

// Transforms a vector from view space to world space
float3 TransformViewVectorToWorld(MaterialInput input, float3 viewVector)
{
	return mul((float3x3)ViewMatrix, viewVector);
}

// Transforms a vector from local space to world space
float3 TransformLocalVectorToWorld(MaterialInput input, float3 localVector)
{
	float3x3 localToWorld = (float3x3)WorldMatrix;
	return mul(localVector, localToWorld);
}

// Transforms a vector from local space to world space
float3 TransformWorldVectorToLocal(MaterialInput input, float3 worldVector)
{
	float3x3 localToWorld = (float3x3)WorldMatrix;
	return mul(localToWorld, worldVector);
}

// Gets the current object position (supports instancing)
float3 GetObjectPosition(MaterialInput input)
{
	return WorldMatrix[3].xyz;
}

// Gets the current object size
float3 GetObjectSize(MaterialInput input)
{
	return float3(1, 1, 1);
}

// Gets the current object scale (supports instancing)
float3 GetObjectScale(MaterialInput input)
{
	return float3(1, 1, 1);
}

// Get the current object random value supports instancing)
float GetPerInstanceRandom(MaterialInput input)
{
	return 0;
}

// Get the current object LOD transition dither factor (supports instancing)
float GetLODDitherFactor(MaterialInput input)
{
	return 0;
}

// Gets the interpolated vertex color (in linear space)
float4 GetVertexColor(MaterialInput input)
{
	return 1;
}

@8

// Get material properties function (for pixel shader)
Material GetMaterialPS(MaterialInput input)
{
@4
}

// Input macro specified by the material: DECAL_BLEND_MODE

#define DECAL_BLEND_MODE_TRANSLUCENT 0
#define DECAL_BLEND_MODE_STAIN       1
#define DECAL_BLEND_MODE_NORMAL      2
#define DECAL_BLEND_MODE_EMISSIVE    3

// Vertex Shader function for decals rendering
META_VS(true, FEATURE_LEVEL_ES2)
META_VS_IN_ELEMENT(POSITION, 0, R32G32B32_FLOAT, 0, 0, PER_VERTEX, 0, true)
void VS_Decal(in float3 Position : POSITION0, out float4 SvPosition : SV_Position)
{
	// Compute world space vertex position
	float3 worldPosition = mul(float4(Position.xyz, 1), WorldMatrix).xyz;

	// Compute clip space position
	SvPosition = mul(float4(worldPosition.xyz, 1), ViewProjectionMatrix);
}

// Pixel Shader function for decals rendering
META_PS(true, FEATURE_LEVEL_ES2)
void PS_Decal(
	in float4 SvPosition : SV_Position
	, out float4 Out0 : SV_Target0
#if DECAL_BLEND_MODE == DECAL_BLEND_MODE_TRANSLUCENT
	, out float4 Out1 : SV_Target1
#if USE_NORMAL || USE_EMISSIVE
	, out float4 Out2 : SV_Target2
#endif
#if USE_NORMAL && USE_EMISSIVE
	, out float4 Out3 : SV_Target3
#endif
#endif
	)
{
	float2 screenUV = SvPosition.xy * ScreenSize.zw;
	SvPosition.z = SAMPLE_RT(DepthBuffer, screenUV).r;

	float4 positionHS = mul(float4(SvPosition.xyz, 1), SVPositionToWorld);
	float3 positionWS = positionHS.xyz / positionHS.w;
	float3 positionOS = mul(float4(positionWS, 1), InvWorld).xyz;

	clip(0.5 - abs(positionOS.xyz));
	float2 decalUVs = positionOS.xz + 0.5f;

	// Setup material input
	MaterialInput materialInput = (MaterialInput)0;
	materialInput.WorldPosition = positionWS;
	materialInput.TexCoord = decalUVs;
	materialInput.TwoSidedSign = 1;
	materialInput.SvPosition = SvPosition;
	
	// Build tangent to world transformation matrix
	float3 ddxWp = ddx(positionWS);
	float3 ddyWp = ddy(positionWS);
	materialInput.TBN[0] = normalize(ddyWp);
	materialInput.TBN[1] = normalize(ddxWp);
	materialInput.TBN[2] = normalize(cross(ddxWp, ddyWp));

	// Sample material
	Material material = GetMaterialPS(materialInput);

	// Masking
#if MATERIAL_MASKED
	clip(material.Mask - MATERIAL_MASK_THRESHOLD);
#endif

	// Set the output
#if DECAL_BLEND_MODE == DECAL_BLEND_MODE_TRANSLUCENT
	// GBuffer0
	Out0 = float4(material.Color, material.Opacity);
	// GBuffer2
	Out1 = float4(material.Roughness, material.Metalness, material.Specular, material.Opacity);
#if USE_EMISSIVE
	// Light Buffer
	Out2 = float4(material.Emissive, material.Opacity);
#if USE_NORMAL
	// GBuffer1
	Out3 = float4(material.WorldNormal * 0.5f + 0.5f, material.Opacity);
#endif
#elif USE_NORMAL
	// GBuffer1
	Out2 = float4(material.WorldNormal * 0.5f + 0.5f, material.Opacity);
#endif
#elif DECAL_BLEND_MODE == DECAL_BLEND_MODE_STAIN
	Out0 = float4(material.Color, material.Opacity);
#elif DECAL_BLEND_MODE == DECAL_BLEND_MODE_NORMAL
	Out0 = float4(material.WorldNormal * 0.5f + 0.5f, material.Opacity);
#elif DECAL_BLEND_MODE == DECAL_BLEND_MODE_EMISSIVE
	Out0 = float4(material.Emissive * material.Opacity, material.Opacity);
#else
	#error "Invalid decal blending mode"
#endif
}

@9

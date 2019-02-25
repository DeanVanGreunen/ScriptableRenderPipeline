using System.Collections.Generic;
using UnityEngine;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing.Controls;
using UnityEngine.Experimental.Rendering.HDPipeline;
using System;
using System.Linq;
using UnityEngine.Rendering;

namespace UnityEditor.Experimental.Rendering.HDPipeline
{
    [Title("Input", "High Definition Render Pipeline", "Parallax Occlusion Mapping")]
    class ParallaxOcclusionMappingNode : AbstractMaterialNode, IGeneratesBodyCode, IGeneratesFunction // TODO: require view camera vector
    {
        public ParallaxOcclusionMappingNode()
        {
            name = "Parallax Occlusion Mapping";
            UpdateNodeAfterDeserialization();
        }

        public override string documentationURL
        {
            // This still needs to be added.
            get { return "https://github.com/Unity-Technologies/ShaderGraph/wiki/Parallax-Occlusion-Mapping-Node"; }
        }

        // [SerializeField] float      m_Lod;
        // [SerializeField] float      m_LodThreshold;
        // [SerializeField] float      m_Amplitude;
        // [SerializeField] int        m_numStep;
        // [SerializeField] Vector2    m_UVs;

        // Input slots
        private const int kHeightmapSlotId = 2;
        private const string kHeightmapSlotName = "Heightmap";
        private const int kHeightmapSamplerSlotId = 3;
        private const string kHeightmapSamplerSlotName = "HeightmapSampler";
        private const int kAmplitudeSlotId = 4;
        private const string kAmplitudeSlotName = "Amplitude";
        private const int kStepsSlotId = 5;
        private const string kStepsSlotName = "Steps";
        private const int kUVsSlotId = 6;
        private const string kUVsSlotName = "UVs";
        private const int kLodSlotId = 7;
        private const string kLodSlotName = "Lod";
        private const int kLodThresholdSlotId = 8;
        private const string kLodThresholdSlotName = "LodThreshold";
        private const int kViewDirTSSlotId = 9;
        private const string kViewDirTSSlotName = "ViewDirTS";


        // Output slots
        private const int kPixelDepthOffsetOutputSlotId = 0;
        private const string kPixelDepthOffsetOutputSlotName = "PixelDepthOffset";
        private const int kParallaxUVsOutputSlotId = 1;
        private const string kParallaxUVsOutputSlotName = "ParallaxUVs";

        public override bool hasPreview { get { return false; } }

        public sealed override void UpdateNodeAfterDeserialization()
        {
            AddSlot(new Texture2DInputMaterialSlot(kHeightmapSlotId, kHeightmapSlotName, kHeightmapSlotName, ShaderStageCapability.Fragment));
            AddSlot(new SamplerStateMaterialSlot(kHeightmapSamplerSlotId, kHeightmapSamplerSlotName, kHeightmapSamplerSlotName, SlotType.Input));
            AddSlot(new Vector1MaterialSlot(kAmplitudeSlotId, kAmplitudeSlotName, kAmplitudeSlotName, SlotType.Input, 1.0f, ShaderStageCapability.Fragment));
            AddSlot(new Vector1MaterialSlot(kStepsSlotId, kStepsSlotName, kStepsSlotName, SlotType.Input, 5.0f, ShaderStageCapability.Fragment));
            AddSlot(new Vector2MaterialSlot(kUVsSlotId, kUVsSlotName, kUVsSlotName, SlotType.Input, Vector2.zero, ShaderStageCapability.Fragment));
            AddSlot(new Vector1MaterialSlot(kLodSlotId, kLodSlotName, kLodSlotName, SlotType.Input, 0.0f, ShaderStageCapability.Fragment));
            AddSlot(new Vector1MaterialSlot(kLodThresholdSlotId, kLodThresholdSlotName, kLodThresholdSlotName, SlotType.Input, 0.0f, ShaderStageCapability.Fragment));
            AddSlot(new Vector3MaterialSlot(kViewDirTSSlotId, kViewDirTSSlotName, kViewDirTSSlotName, SlotType.Input, Vector3.zero, ShaderStageCapability.Fragment));

            AddSlot(new Vector1MaterialSlot(kPixelDepthOffsetOutputSlotId, kPixelDepthOffsetOutputSlotName, kPixelDepthOffsetOutputSlotName, SlotType.Output, 0.0f, ShaderStageCapability.Fragment));
            AddSlot(new Vector2MaterialSlot(kParallaxUVsOutputSlotId, kParallaxUVsOutputSlotName, kParallaxUVsOutputSlotName, SlotType.Output, Vector2.zero, ShaderStageCapability.Fragment));
            RemoveSlotsNameNotMatching(new[] {
                kPixelDepthOffsetOutputSlotId,
                kParallaxUVsOutputSlotId,
                kHeightmapSlotId,
                kHeightmapSamplerSlotId,
                kAmplitudeSlotId,
                kStepsSlotId,
                kUVsSlotId,
                kLodSlotId,
                kLodThresholdSlotId,
                kViewDirTSSlotId
            });
        }

        string GetFunctionName()
        {
            return "Unity_HDRP_ParallaxOcclusionMapping";
        }

        public override void ValidateNode()
        {
            var textureSlot = FindInputSlot<Texture2DInputMaterialSlot>(kHeightmapSlotId);
            textureSlot.defaultType = TextureShaderProperty.DefaultType.Black;

            base.ValidateNode();
        }

        public void GenerateNodeFunction(FunctionRegistry registry, GraphContext graphContext, GenerationMode generationMode)
        {
            string perPixelDisplacementInclude = @"#include ""Packages/com.unity.render-pipelines.core/ShaderLibrary/PerPixelDisplacement.hlsl""";

            // Texture sample inputs
            var samplerSlot = FindInputSlot<MaterialSlot>(kHeightmapSamplerSlotId);
            var edgesSampler = owner.GetEdges(samplerSlot.slotReference);
            var heightmap = GetSlotValue(kHeightmapSlotId, generationMode);

            registry.ProvideFunction(GetFunctionName(), s =>
                {
                    s.AppendLine("{0}3 GetDisplacementObjectScale(bool vertexDisplacement)", precision);
                    using (s.BlockScope())
                    {
                        s.Append(@"
float3 objectScale = float3(1.0, 1.0, 1.0);
float4x4 worldTransform = GetWorldToObjectMatrix();

objectScale.x = length(float3(worldTransform._m00, worldTransform._m01, worldTransform._m02));
objectScale.z = length(float3(worldTransform._m20, worldTransform._m21, worldTransform._m22));

return objectScale;
");
                    }

                    // requirements to call the ParallaxOcclusionMapping function
                    s.AppendLine("struct PerPixelHeightDisplacementParam");
                    using (s.BlockSemicolonScope())
                    {
                        s.AppendLine("{0}2 uv;", precision);
                    }
                    s.AppendLine("{0} ComputePerPixelHeightDisplacement({0}2 texOffsetCurrent, {0} lod, PerPixelHeightDisplacementParam param)", precision);
                    using (s.BlockScope())
                    {
                        s.AppendLine("return SAMPLE_TEXTURE2D_LOD({0}, {1}, param.uv + texOffsetCurrent, lod).r;",
                            heightmap,
                            edgesSampler.Any() ? GetSlotValue(kHeightmapSamplerSlotId, generationMode) : "sampler" + heightmap);
                    }
                    s.Append(perPixelDisplacementInclude);
                });
        }

        public void GenerateNodeCode(ShaderGenerator visitor, GraphContext graphContext, GenerationMode generationMode)
        {
            string amplitude = GetSlotValue(kAmplitudeSlotId, generationMode);
            string steps = GetSlotValue(kStepsSlotId, generationMode);
            string uvs = GetSlotValue(kUVsSlotId, generationMode);
            string lod = GetSlotValue(kLodSlotId, generationMode);
            string lodThreshold = GetSlotValue(kLodThresholdSlotId, generationMode);
            string viewDirTS = GetSlotValue(kViewDirTSSlotId, generationMode);

            string fieldName = GetVariableNameForSlot(kViewDirTSSlotId);

// TODO: cleanup
            visitor.AddShaderChunk(String.Format(@"
{0}3 viewDirTS = " + viewDirTS + @" * GetDisplacementObjectScale(false).xzy;

// Transform the view vector into the UV space.
{0}3 viewDirUV    = normalize({0}3(viewDirTS.xy * " + amplitude + @", viewDirTS.z)); // TODO: skip normalize

PerPixelHeightDisplacementParam {1};
{1}.uv = {2};", precision, fieldName, uvs));
            visitor.AddShaderChunk(String.Format(@"
{0} {7};
{0}2 {1} = ParallaxOcclusionMapping({2}, {3}, {4}, {5}, {6}, {7});",
                precision,
                GetVariableNameForSlot(kParallaxUVsOutputSlotId),
                lod,
                lodThreshold,
                steps,
                "viewDirUV",
                fieldName,
                GetVariableNameForSlot(kPixelDepthOffsetOutputSlotId)
                ));
        }
    }
}

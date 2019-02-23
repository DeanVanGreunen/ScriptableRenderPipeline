using System;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine.Rendering.PostProcessing;

namespace UnityEngine.Rendering.LWRP
{
    // Note: Spaced built-in events so we can add events in between them
    // We need to leave room as we sort render passes based on event.
    // Users can also inject render pass events in a specific point by doing RenderPassEvent + offset
    /// <summary>
    /// Controls when the render pass should execute.
    /// </summary>
    public enum RenderPassEvent
    {
        BeforeRendering = 0,
        BeforeRenderingOpaques = 10,
        AfterRenderingOpaques = 20,
        AfterRenderingSkybox = 30,
        AfterRenderingTransparentPasses = 40,
        AfterRendering = 50,
    }

    /// <summary>
    /// Inherit from this class to perform custom rendering in the Lightweight Render Pipeline.
    /// </summary>
    public abstract class ScriptableRenderPass : IComparable<ScriptableRenderPass>
    {
        // TODO: Add support to 4 MRT here.
        struct ScriptableRenderPassDescriptor
        {
            public ScriptableRenderPassDescriptor(int width, int height, int msaaSamples)
            {
                this.width = width;
                this.height = height;
                this.msaaSamples = msaaSamples;
            }

            public int width;
            public int height;
            public int msaaSamples;
        }

        internal int m_ColorAttachmentId = -1;
        internal int m_DepthAttachmentId = -1;
        internal RenderTextureDescriptor m_ColorAttachmentDescriptor;
        internal RenderTextureDescriptor m_DepthAttachmentDescriptor;
        internal FilterMode m_ColorFilterMode = FilterMode.Bilinear;
        internal FilterMode m_DepthFilterMode = FilterMode.Bilinear;
        ScriptableRenderPassDescriptor m_Descriptor;

        public RenderPassEvent renderPassEvent { get; set; }

        public int targetWidth
        {
            get => m_Descriptor.width;
        }

        public int targetHeight
        {
            get => m_Descriptor.height;
        }

        public int targetMsaa
        {
            get => m_Descriptor.msaaSamples;
        }

        public ScriptableRenderPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            m_Descriptor = new ScriptableRenderPassDescriptor(-1, -1, -1);
        }

        List<ShaderTagId> m_ShaderTagIDs = new List<ShaderTagId>();

        static List<ShaderTagId> m_LegacyShaderPassNames = new List<ShaderTagId>()
        {
            new ShaderTagId("Always"),
            new ShaderTagId("ForwardBase"),
            new ShaderTagId("PrepassBase"),
            new ShaderTagId("Vertex"),
            new ShaderTagId("VertexLMRGBM"),
            new ShaderTagId("VertexLM"),
        };

        static Material s_ErrorMaterial;
        static Material errorMaterial
        {
            get
            {
                if (s_ErrorMaterial == null)
                    s_ErrorMaterial = new Material(Shader.Find("Hidden/InternalErrorShader"));

                return s_ErrorMaterial;
            }
        }

        static Mesh s_FullscreenMesh = null;
        public static Mesh fullscreenMesh
        {
            get
            {
                if (s_FullscreenMesh != null)
                    return s_FullscreenMesh;

                float topV = 1.0f;
                float bottomV = 0.0f;

                s_FullscreenMesh = new Mesh { name = "Fullscreen Quad" };
                s_FullscreenMesh.SetVertices(new List<Vector3>
                {
                    new Vector3(-1.0f, -1.0f, 0.0f),
                    new Vector3(-1.0f,  1.0f, 0.0f),
                    new Vector3(1.0f, -1.0f, 0.0f),
                    new Vector3(1.0f,  1.0f, 0.0f)
                });

                s_FullscreenMesh.SetUVs(0, new List<Vector2>
                {
                    new Vector2(0.0f, bottomV),
                    new Vector2(0.0f, topV),
                    new Vector2(1.0f, bottomV),
                    new Vector2(1.0f, topV)
                });

                s_FullscreenMesh.SetIndices(new[] { 0, 1, 2, 2, 1, 3 }, MeshTopology.Triangles, 0, false);
                s_FullscreenMesh.UploadMeshData(true);
                return s_FullscreenMesh;
            }
        }

        static PostProcessRenderContext m_PostProcessRenderContext;
        internal static PostProcessRenderContext postProcessRenderContext
        {
            get
            {
                if (m_PostProcessRenderContext == null)
                    m_PostProcessRenderContext = new PostProcessRenderContext();

                return m_PostProcessRenderContext;
            }
        }

        public void ConfigureTarget(int width, int height, int msaaSamples)
        {
            m_Descriptor = new ScriptableRenderPassDescriptor(width, height, msaaSamples);
        }

        public void BindColorSurface(int rtId, RenderTextureDescriptor rtDescriptor, FilterMode filterMode)
        {
            m_ColorAttachmentId = rtId;
            m_ColorAttachmentDescriptor = rtDescriptor;
            m_ColorFilterMode = filterMode;
        }

        public void BindDepthSurface(int rtId, RenderTextureDescriptor rtDescriptor, FilterMode filterMode)
        {
            m_DepthAttachmentId = rtId;
            m_DepthAttachmentDescriptor = rtDescriptor;
            m_DepthFilterMode = filterMode;
        }

        /// <summary>
        /// Cleanup any allocated data that was created during the execution of the pass.
        /// </summary>
        /// <param name="cmd">Use this CommandBuffer to cleanup any generated data</param>
        public virtual void FrameCleanup(CommandBuffer cmd)
        {}

        /// <summary>
        /// Implement this to conditionally enqueue the pass depending on rendering state for the current frame.
        /// By default a render pass will always be enqueued for execution.
        /// </summary>
        /// <param name="renderingData">Current rendering state information</param>
        /// <returns></returns>
        public virtual bool ShouldExecute(ref RenderingData renderingData)
        {
            return true;
        }

        /// <summary>
        /// Execute the pass. This is where custom rendering occurs. Specific details are left to the implementation
        /// </summary>
        /// <param name="renderer">The currently executing renderer. Contains configuration for the current execute call.</param>
        /// <param name="context">Use this render context to issue any draw commands during execution</param>
        /// <param name="renderingData">Current rendering state information</param>
        public abstract void Execute(ScriptableRenderContext context, ref RenderingData renderingData);

        public int CompareTo(ScriptableRenderPass other)
        {
            return (int)renderPassEvent - (int)other.renderPassEvent;
        }

        protected void RegisterShaderPassName(string passName)
        {
            m_ShaderTagIDs.Add(new ShaderTagId(passName));
        }

        /// <summary>
        /// Renders PostProcessing.
        /// </summary>
        /// <param name="cmd">A command buffer to execute post processing commands.</param>
        /// <param name="cameraData">Camera rendering data.</param>
        /// <param name="colorFormat">Color format of the source render target id.</param>
        /// <param name="source">Source render target id.</param>
        /// <param name="dest">Destination render target id.</param>
        /// <param name="opaqueOnly">Should only execute after opaque post processing effects.</param>
        /// <param name="flip">Should flip the image vertically.</param>
        protected void RenderPostProcess(CommandBuffer cmd, ref CameraData cameraData, RenderTextureFormat colorFormat, RenderTargetIdentifier source, RenderTargetIdentifier dest, bool opaqueOnly, bool flip)
        {
            Camera camera = cameraData.camera;
            postProcessRenderContext.Reset();
            postProcessRenderContext.camera = camera;
            postProcessRenderContext.source = source;
            postProcessRenderContext.sourceFormat = colorFormat;
            postProcessRenderContext.destination = dest;
            postProcessRenderContext.command = cmd;
            postProcessRenderContext.flip = flip;

            if (opaqueOnly)
                cameraData.postProcessLayer.RenderOpaqueOnly(postProcessRenderContext);
            else
                cameraData.postProcessLayer.Render(postProcessRenderContext);
        }

        /// <summary>
        /// Creates <c>DrawingSettings</c> based on current rendering state.
        /// </summary>
        /// <param name="renderingData">Current rendering state.</param>
        /// <param name="sortingCriteria">Criteria to sort objects being rendered.</param>
        /// <returns></returns>
        /// <seealso cref="DrawingSettings"/>
        protected DrawingSettings CreateDrawingSettings(ref RenderingData renderingData, SortingCriteria sortingCriteria)
        {
            Camera camera = renderingData.cameraData.camera;
            SortingSettings sortingSettings = new SortingSettings(camera) { criteria = sortingCriteria };
            DrawingSettings settings = new DrawingSettings(m_ShaderTagIDs[0], sortingSettings)
            {
                perObjectData = renderingData.perObjectData,
                enableInstancing = true,
                mainLightIndex = renderingData.lightData.mainLightIndex,
                enableDynamicBatching = renderingData.supportsDynamicBatching,
            };
            for (int i = 1; i < m_ShaderTagIDs.Count; ++i)
                settings.SetShaderPassName(i, m_ShaderTagIDs[i]);
            return settings;
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal void RenderObjectsWithError(ScriptableRenderContext context, ref CullingResults cullResults, Camera camera, FilteringSettings filterSettings, SortingCriteria sortFlags)
        {
            SortingSettings sortingSettings = new SortingSettings(camera) { criteria = sortFlags };
            DrawingSettings errorSettings = new DrawingSettings(m_LegacyShaderPassNames[0], sortingSettings)
            {
                perObjectData = PerObjectData.None,
                overrideMaterial = errorMaterial,
                overrideMaterialPassIndex = 0
            };
            for (int i = 1; i < m_LegacyShaderPassNames.Count; ++i)
                errorSettings.SetShaderPassName(i, m_LegacyShaderPassNames[i]);

            context.DrawRenderers(cullResults, ref errorSettings, ref filterSettings);
        }

        public void SetRenderTarget(
            CommandBuffer cmd,
            RenderTargetIdentifier colorAttachment,
            RenderBufferLoadAction colorLoadAction,
            RenderBufferStoreAction colorStoreAction,
            ClearFlag clearFlags,
            Color clearColor,
            TextureDimension dimension)
        {
            if (dimension == TextureDimension.Tex2DArray)
                CoreUtils.SetRenderTarget(cmd, colorAttachment, clearFlags, clearColor, 0, CubemapFace.Unknown, -1);
            else
                CoreUtils.SetRenderTarget(cmd, colorAttachment, colorLoadAction, colorStoreAction, clearFlags, clearColor);
        }

        public void SetRenderTarget(
            CommandBuffer cmd,
            RenderTargetIdentifier colorAttachment,
            RenderBufferLoadAction colorLoadAction,
            RenderBufferStoreAction colorStoreAction,
            RenderTargetIdentifier depthAttachment,
            RenderBufferLoadAction depthLoadAction,
            RenderBufferStoreAction depthStoreAction,
            ClearFlag clearFlags,
            Color clearColor,
            TextureDimension dimension)
        {
            if (depthAttachment == BuiltinRenderTextureType.CameraTarget)
            {
                SetRenderTarget(cmd, colorAttachment, colorLoadAction, colorStoreAction, clearFlags, clearColor,
                    dimension);
            }
            else
            {
                if (dimension == TextureDimension.Tex2DArray)
                    CoreUtils.SetRenderTarget(cmd, colorAttachment, depthAttachment,
                        clearFlags, clearColor, 0, CubemapFace.Unknown, -1);
                else
                    CoreUtils.SetRenderTarget(cmd, colorAttachment, colorLoadAction, colorStoreAction,
                        depthAttachment, depthLoadAction, depthStoreAction, clearFlags, clearColor);
            }
        }
    }
}

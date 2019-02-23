namespace UnityEngine.Rendering.LWRP
{
    /// <summary>
    /// Render all opaque forward objects into the given color and depth target
    ///
    /// You can use this pass to render objects that have a material and/or shader
    /// with the pass names LightweightForward or SRPDefaultUnlit. The pass only
    /// renders objects in the rendering queue range of Opaque objects.
    /// </summary>
    internal class RenderOpaqueForwardPass : ScriptableRenderPass
    {
        RenderTargetHandle colorAttachmentHandle { get; set; }
        RenderTargetHandle depthAttachmentHandle { get; set; }
        RenderTextureDescriptor descriptor { get; set; }
        ClearFlag clearFlag { get; set; }
        Color clearColor { get; set; }

        FilteringSettings m_FilteringSettings;
        string m_ProfilerTag = "Render Opaques";

        public RenderOpaqueForwardPass(RenderPassEvent evt, RenderQueueRange renderQueueRange, LayerMask layerMask)
        {
            RegisterShaderPassName("LightweightForward");
            RegisterShaderPassName("SRPDefaultUnlit");
            renderPassEvent = evt;
            m_FilteringSettings = new FilteringSettings(renderQueueRange, layerMask);
        }

        /// <summary>
        /// Configure the pass before execution
        /// </summary>
        /// <param name="baseDescriptor">Current target descriptor</param>
        /// <param name="colorAttachmentHandle">Color attachment to render into</param>
        /// <param name="depthAttachmentHandle">Depth attachment to render into</param>
        /// <param name="clearFlag">Camera clear flag</param>
        /// <param name="clearColor">Camera clear color</param>
        /// <param name="configuration">Specific render configuration</param>
        public void Setup(
            RenderTextureDescriptor baseDescriptor,
            RenderTargetHandle colorAttachmentHandle,
            RenderTargetHandle depthAttachmentHandle,
            ClearFlag clearFlag,
            Color clearColor)
        {
            this.colorAttachmentHandle = colorAttachmentHandle;
            this.depthAttachmentHandle = depthAttachmentHandle;
            this.clearColor = CoreUtils.ConvertSRGBToActiveColorSpace(clearColor);
            this.clearFlag = clearFlag;
            descriptor = baseDescriptor;
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
            using (new ProfilingSample(cmd, m_ProfilerTag))
            {
                //// When ClearFlag.None that means this is not the first render pass to write to camera target.
                //// In that case we set loadOp for both color and depth as RenderBufferLoadAction.Load
                //RenderBufferLoadAction loadOp = clearFlag != ClearFlag.None ? RenderBufferLoadAction.DontCare : RenderBufferLoadAction.Load;
                //RenderBufferStoreAction storeOp = RenderBufferStoreAction.Store;

                //SetRenderTarget(cmd, colorAttachmentHandle.Identifier(), loadOp, storeOp,
                //    depthAttachmentHandle.Identifier(), loadOp, storeOp, clearFlag, clearColor, descriptor.dimension);

                //// TODO: We need a proper way to handle multiple camera/ camera stack. Issue is: multiple cameras can share a same RT
                //// (e.g, split screen games). However devs have to be dilligent with it and know when to clear/preserve color.
                //// For now we make it consistent by resolving viewport with a RT until we can have a proper camera management system
                ////if (colorAttachmentHandle == -1 && !cameraData.isDefaultViewport)
                ////    cmd.SetViewport(camera.pixelRect);
                //context.ExecuteCommandBuffer(cmd);
                //cmd.Clear();

                Camera camera = renderingData.cameraData.camera;
                var sortFlags = renderingData.cameraData.defaultOpaqueSortFlags;
                var drawSettings = CreateDrawingSettings(ref renderingData, sortFlags);
                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref m_FilteringSettings);

                // Render objects that did not match any shader pass with error shader
                RenderObjectsWithError(context, ref renderingData.cullResults, camera, m_FilteringSettings, SortingCriteria.None);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}

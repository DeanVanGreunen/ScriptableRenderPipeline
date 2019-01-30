using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEngine.Assertions;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using INode = UnityEditor.Graphing.INode;
using Object = UnityEngine.Object;

namespace UnityEditor.ShaderGraph.Drawing
{
    class PreviewManager : IDisposable
    {
        AbstractMaterialGraph m_Graph;
        MessageManager m_Messenger;
        List<PreviewRenderData> m_RenderDatas = new List<PreviewRenderData>();
        PreviewRenderData m_MasterRenderData;
        List<Identifier> m_Identifiers = new List<Identifier>();
        HashSet<INode> m_NodesToUpdate = new HashSet<INode>();
        HashSet<INode> m_NodesToDraw = new HashSet<INode>();
        HashSet<INode> m_TimedNodes = new HashSet<INode>();
        bool m_RefreshTimedNodes;
        
        PreviewSceneResources m_SceneResources;
        Texture2D m_ErrorTexture;
        Vector2? m_NewMasterPreviewSize;

        public PreviewRenderData masterRenderData
        {
            get { return m_MasterRenderData; }
        }

        public PreviewManager(AbstractMaterialGraph graph, MessageManager messenger)
        {
            m_Graph = graph;
            m_Messenger = messenger;
            m_ErrorTexture = GenerateFourSquare(Color.magenta, Color.black);
            m_SceneResources = new PreviewSceneResources();
            m_MasterRenderData = new PreviewRenderData
            {
                renderTexture = new RenderTexture(400, 400, 16, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default) {hideFlags = HideFlags.HideAndDontSave}
            };
            m_MasterRenderData.renderTexture.Create();

            foreach (var node in m_Graph.GetNodes<INode>())
                AddPreview(node);
        }

        static Texture2D GenerateFourSquare(Color c1, Color c2)
        {
            var tex = new Texture2D(2, 2);
            tex.SetPixel(0, 0, c1);
            tex.SetPixel(0, 1, c2);
            tex.SetPixel(1, 0, c2);
            tex.SetPixel(1, 1, c1);
            tex.filterMode = FilterMode.Point;
            tex.Apply();
            return tex;
        }
        
        public void ResizeMasterPreview(Vector2 newSize)
        {
            m_NewMasterPreviewSize = newSize;
        }

        public PreviewRenderData GetPreview(AbstractMaterialNode node)
        {
            return m_RenderDatas[node.tempId.index];
        }

        void AddPreview(INode node)
        {
            var shaderData = new PreviewShaderData
            {
                node = node,
                isCompiling = false,
                hasError = false,
                shader = ShaderUtil.CreateShaderAsset(k_EmptyShader, false)
            };
            shaderData.shader.hideFlags = HideFlags.HideAndDontSave;
            shaderData.mat = new Material(shaderData.shader) {hideFlags = HideFlags.HideAndDontSave};
            shaderData.props = new MaterialPropertyBlock();
            
            PreviewRenderData renderData;
            if (masterRenderData.shaderData == null &&
                (node is IMasterNode || node is SubGraphOutputNode))
            {
                renderData = masterRenderData;
            }
            else
            {
                renderData = new PreviewRenderData
                {
                    renderTexture =
                        new RenderTexture(200, 200, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                        {
                            hideFlags = HideFlags.HideAndDontSave
                        }
                };
                renderData.renderTexture.Create();
            }
            renderData.shaderData = shaderData;
            Set(m_Identifiers, node.tempId, node.tempId);
            Set(m_RenderDatas, node.tempId, renderData);
            node.RegisterCallback(OnNodeModified);

            if (node.RequiresTime())
            {
                m_RefreshTimedNodes = true;
            }
            
            m_NodesToUpdate.Add(node);
        }

        void OnNodeModified(INode node, ModificationScope scope)
        {
            if (scope == ModificationScope.Topological ||
                scope == ModificationScope.Graph)
            {
                m_NodesToUpdate.Add(node);

                m_RefreshTimedNodes = true;
            }
            else if (scope == ModificationScope.Node)
            {
                m_NodesToDraw.Add(node);
            }
        }

        Stack<INode> m_NodeWave = new Stack<INode>();
        List<IEdge> m_Edges = new List<IEdge>();
        List<MaterialSlot> m_Slots = new List<MaterialSlot>();
        List<INode> m_NextLevelNodes = new List<INode>();

        enum PropagationDirection
        {
            Upstream,
            Downstream
        }

        void PropagateNodeList(ICollection<INode> nodes, PropagationDirection dir)
        {
            m_NodeWave.Clear();
            foreach (var node in nodes)
                m_NodeWave.Push(node);

            while (m_NodeWave.Count > 0)
            {
                var node = m_NodeWave.Pop();
                if (node == null)
                    continue;

                m_NextLevelNodes.Clear();
                GetConnectedNodes(node, dir, m_NextLevelNodes);
                m_NextLevelNodes.ForEach(n =>
                {
                    nodes.Add(n);
                    m_NodeWave.Push(n);
                });
            }
        }

        void GetConnectedNodes(INode node, PropagationDirection dir, ICollection<INode> connections)
        {
            // Loop through all nodes that the node feeds into.
            m_Slots.Clear();
            if (dir == PropagationDirection.Downstream)
                node.GetOutputSlots(m_Slots);
            else
                node.GetInputSlots(m_Slots);
            foreach (var slot in m_Slots)
            {
                m_Edges.Clear();
                m_Graph.GetEdges(slot.slotReference, m_Edges);
                foreach (var edge in m_Edges)
                {
                    // We look at each node we feed into.
                    var connectedSlot = (dir == PropagationDirection.Downstream) ? edge.inputSlot : edge.outputSlot;
                    var connectedNodeGuid = connectedSlot.nodeGuid;
                    var connectedNode = m_Graph.GetNodeFromGuid(connectedNodeGuid);

                    // If the input node is already in the set, we don't need to process it.
                    if (connections.Contains(connectedNode))
                        continue;

                    // Add the node to the set, and to the wavefront such that we can process the nodes that it feeds into.
                    connections.Add(connectedNode);
                }
            }
        }

        public void HandleGraphChanges()
        {
            foreach (var node in m_Graph.removedNodes)
            {
                DestroyPreview(node.tempId);
            }

            m_Messenger.ClearNodesFromProvider(this, m_Graph.removedNodes);

            foreach (var node in m_Graph.addedNodes)
            {
                AddPreview(node);
            }

            foreach (var edge in m_Graph.removedEdges)
            {
                var node = m_Graph.GetNodeFromGuid(edge.inputSlot.nodeGuid);
                if (node != null)
                {
                    m_NodesToUpdate.Add(node);
                    m_RefreshTimedNodes = true;
                }
            }
            foreach (var edge in m_Graph.addedEdges)
            {
                var node = m_Graph.GetNodeFromGuid(edge.inputSlot.nodeGuid);
                if(node != null)
                {
                    m_NodesToUpdate.Add(node);
                    m_RefreshTimedNodes = true;
                }
            }
        }

        List<PreviewProperty> m_PreviewProperties = new List<PreviewProperty>();
        List<INode> m_PropertyNodes = new List<INode>();

        void CollectShaderProperties(AbstractMaterialNode node, PreviewRenderData renderData)
        {
            m_PreviewProperties.Clear();
            m_PropertyNodes.Clear();
            renderData.shaderData.props.Clear();

            m_PropertyNodes.Add(node);
            PropagateNodeList(m_PropertyNodes, PropagationDirection.Upstream);

            foreach (var propNode in m_PropertyNodes)
            {
                (propNode as AbstractMaterialNode).CollectPreviewMaterialProperties(m_PreviewProperties);
            }

            foreach (var prop in m_Graph.properties)
                m_PreviewProperties.Add(prop.GetPreviewMaterialProperty());

            foreach (var previewProperty in m_PreviewProperties)
                renderData.shaderData.props.SetPreviewProperty(previewProperty);
        }

        List<PreviewRenderData> m_RenderList2D = new List<PreviewRenderData>();
        List<PreviewRenderData> m_RenderList3D = new List<PreviewRenderData>();

        public void RenderPreviews()
        {
            UpdateShaders();
            UpdateTimedNodeList();
            
            PropagateNodeList(m_NodesToDraw, PropagationDirection.Downstream);
            
            foreach (var node in m_TimedNodes.Union(m_NodesToDraw).Cast<AbstractMaterialNode>())
            {
                if(node == null || !node.hasPreview || !node.previewExpanded)
                    continue;
                
                var renderData = GetRenderData(node.tempId);
                renderData.previewMode = PreviewMode.Preview3D;
                if (node.previewMode == PreviewMode.Preview2D)
                {
                    renderData.previewMode = PreviewMode.Preview2D;
                }

                CollectShaderProperties(node, renderData);

                if (renderData.shaderData.shader == null)
                {
                    renderData.texture = null;
                    renderData.NotifyPreviewChanged();
                    continue;
                }
                if (renderData.shaderData.hasError)
                {
                    renderData.texture = m_ErrorTexture;
                    renderData.NotifyPreviewChanged();
                    continue;
                }
                
                if (renderData.previewMode == PreviewMode.Preview2D)
                    m_RenderList2D.Add(renderData);
                else
                    m_RenderList3D.Add(renderData);
            }

            var time = Time.realtimeSinceStartup;
            EditorUtility.SetCameraAnimateMaterialsTime(m_SceneResources.camera, time);

            m_SceneResources.light0.enabled = true;
            m_SceneResources.light0.intensity = 1.0f;
            m_SceneResources.light0.transform.rotation = Quaternion.Euler(50f, 50f, 0);
            m_SceneResources.light1.enabled = true;
            m_SceneResources.light1.intensity = 1.0f;
            m_SceneResources.camera.clearFlags = CameraClearFlags.Color;

            // Render 2D previews
            m_SceneResources.camera.transform.position = -Vector3.forward * 2;
            m_SceneResources.camera.transform.rotation = Quaternion.identity;
            m_SceneResources.camera.orthographicSize = 0.5f;
            m_SceneResources.camera.orthographic = true;

            foreach (var renderData in m_RenderList2D)
                RenderPreview(renderData, m_SceneResources.quad, Matrix4x4.identity);

            // Render 3D previews
            m_SceneResources.camera.transform.position = -Vector3.forward * 5;
            m_SceneResources.camera.transform.rotation = Quaternion.identity;
            m_SceneResources.camera.orthographic = false;

            foreach (var renderData in m_RenderList3D)
                RenderPreview(renderData, m_SceneResources.sphere, Matrix4x4.identity);

            var renderMasterPreview = masterRenderData.shaderData != null;
            if (renderMasterPreview)
            {
                CollectShaderProperties(masterRenderData.shaderData.node as AbstractMaterialNode, masterRenderData);

                if (m_NewMasterPreviewSize.HasValue)
                {
                    if (masterRenderData.renderTexture != null)
                        Object.DestroyImmediate(masterRenderData.renderTexture, true);
                    masterRenderData.renderTexture = new RenderTexture((int)m_NewMasterPreviewSize.Value.x, (int)m_NewMasterPreviewSize.Value.y, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { hideFlags = HideFlags.HideAndDontSave };
                    masterRenderData.renderTexture.Create();
                    masterRenderData.texture = masterRenderData.renderTexture;
                    m_NewMasterPreviewSize = null;
                }
                var mesh = m_Graph.previewData.serializedMesh.mesh ? m_Graph.previewData.serializedMesh.mesh :  m_SceneResources.sphere;
                var previewTransform = Matrix4x4.Rotate(m_Graph.previewData.rotation);
                var scale = m_Graph.previewData.scale;
                previewTransform *= Matrix4x4.Scale(scale * Vector3.one * (Vector3.one).magnitude / mesh.bounds.size.magnitude);
                previewTransform *= Matrix4x4.Translate(-mesh.bounds.center);
                RenderPreview(masterRenderData, mesh, previewTransform);
            }

            m_SceneResources.light0.enabled = false;
            m_SceneResources.light1.enabled = false;

            foreach (var renderData in m_RenderList2D)
                renderData.NotifyPreviewChanged();
            foreach (var renderData in m_RenderList3D)
                renderData.NotifyPreviewChanged();
            if (renderMasterPreview)
                masterRenderData.NotifyPreviewChanged();

            m_RenderList2D.Clear();
            m_RenderList3D.Clear();
            m_NodesToDraw.Clear();
        }

        public void ForceShaderUpdate()
        {
            m_RenderDatas.ForEach(data =>
            {
                if (data != null)
                {
                    m_NodesToUpdate.Add(data.shaderData.node);
                }
            });
        }

        void UpdateShaders()
        {
            foreach (var renderData in m_RenderDatas)
            {
                if (renderData != null && renderData.shaderData.isCompiling &&
                    ShaderUtil.IsPassCompiled(renderData.shaderData.mat, 0))
                {
                    renderData.shaderData.isCompiling = false;
                    CheckForErrors(renderData.shaderData);
                    m_NodesToDraw.Add(renderData.shaderData.node);
                }
            }

            if (m_NodesToUpdate.Count == 0)
                return;

            PropagateNodeList(m_NodesToUpdate, PropagationDirection.Downstream);
            // Reset error states for the UI, the shader, and all render data for nodes we're updating
            m_Messenger.ClearNodesFromProvider(this, m_NodesToUpdate);
            var wasAsyncAllowed = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = true;

            foreach (var iNode in m_NodesToUpdate)
            {
                if (iNode == masterRenderData.shaderData.node)
                {
                    UpdateMasterNodeShader();
                    continue;
                }
                
                if (!(iNode is AbstractMaterialNode node) || !node.hasPreview)
                    continue;

                var results = m_Graph.GetPreviewShader(node);

                var renderData = GetRenderData(node.tempId);
                BeginCompile(renderData, results.shader);
            }

            ShaderUtil.allowAsyncCompilation = wasAsyncAllowed;
            m_NodesToUpdate.Clear();
        }

        void BeginCompile(PreviewRenderData renderData, string shaderStr)
        {
            var shaderData = renderData.shaderData;
            ShaderUtil.UpdateShaderAsset(shaderData.shader, shaderStr, false);
            ShaderUtil.CompilePass(shaderData.mat, 0);
            shaderData.isCompiling = true;
            renderData.NotifyPreviewChanged();
        }

        void UpdateTimedNodeList()
        {
            if (!m_RefreshTimedNodes)
                return;
            
            m_TimedNodes.Clear();
            
            foreach (var timeNode in m_Graph.GetNodes<INode>().Where(node => node.RequiresTime()))
            {
                m_TimedNodes.Add(timeNode);
            }
            
            PropagateNodeList(m_TimedNodes, PropagationDirection.Downstream);
            m_RefreshTimedNodes = false;
        }

        void RenderPreview(PreviewRenderData renderData, Mesh mesh, Matrix4x4 transform)
        {
            var node = renderData.shaderData.node as AbstractMaterialNode;
            Assert.IsTrue((node != null && node.hasPreview && node.previewExpanded) || node == masterRenderData.shaderData?.node);
            
            if (renderData.shaderData.hasError)
            {
                renderData.texture = m_ErrorTexture;
                return;
            }

            var previousRenderTexure = RenderTexture.active;

            //Temp workaround for alpha previews...
            var temp = RenderTexture.GetTemporary(renderData.renderTexture.descriptor);
            RenderTexture.active = temp;
            Graphics.Blit(Texture2D.whiteTexture, temp, m_SceneResources.checkerboardMaterial);

            m_SceneResources.camera.targetTexture = temp;
            Graphics.DrawMesh(mesh, transform, renderData.shaderData.mat, 1, m_SceneResources.camera, 0, renderData.shaderData.props, ShadowCastingMode.Off, false, null, false);

            var previousUseSRP = Unsupported.useScriptableRenderPipeline;
            Unsupported.useScriptableRenderPipeline = renderData.shaderData.node is IMasterNode;
            m_SceneResources.camera.Render();
            Unsupported.useScriptableRenderPipeline = previousUseSRP;

            Graphics.Blit(temp, renderData.renderTexture, m_SceneResources.blitNoAlphaMaterial);
            RenderTexture.ReleaseTemporary(temp);

            RenderTexture.active = previousRenderTexure;
            renderData.texture = renderData.renderTexture;
        }

        void CheckForErrors(PreviewShaderData shaderData)
        {
            m_Messenger.ClearNodesFromProvider(this, shaderData.node.ToEnumerable());
            shaderData.hasError = ShaderUtil.ShaderHasError(shaderData.shader);
            if (shaderData.hasError)
            {
                var messages = ShaderUtil.GetShaderMessages(shaderData.shader);
                if (messages.Length > 0)
                {
                    m_Messenger.AddOrAppendError(this, shaderData.node.tempId, messages[0]);
                }
            }
        }
            
        void UpdateMasterNodeShader()
        {
            var shaderData = masterRenderData?.shaderData;
            var masterNode = shaderData?.node as IMasterNode;

            if (masterNode == null)
                return;

            List<PropertyCollector.TextureInfo> configuredTextures;
            shaderData.shaderString = masterNode.GetShader(GenerationMode.Preview, masterNode.name, out configuredTextures);

            var debugOutputPath = DefaultShaderIncludes.GetDebugOutputPath();
            if (!string.IsNullOrEmpty(debugOutputPath))
            {
                File.WriteAllText(debugOutputPath + "/GeneratedShader.shader",
                    (shaderData.shaderString ?? "null").Replace("UnityEngine.MaterialGraph", "Generated"));
            }

            if (string.IsNullOrEmpty(shaderData.shaderString))
            {
                if (shaderData.shader != null)
                {
                    ShaderUtil.ClearShaderMessages(shaderData.shader);
                    Object.DestroyImmediate(shaderData.shader, true);
                    shaderData.shader = null;
                }
                return;
            }

            if (shaderData.shader == null)
            {
                shaderData.shader = ShaderUtil.CreateShaderAsset(shaderData.shaderString, false);
                shaderData.shader.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                ShaderUtil.ClearCachedData(shaderData.shader);
            }
            BeginCompile(masterRenderData, shaderData.shaderString);
        }

        void DestroyRenderData(PreviewRenderData renderData)
        {
            if (renderData.shaderData != null
                && renderData.shaderData.shader != null)
                Object.DestroyImmediate(renderData.shaderData.shader, true);
            if (renderData.renderTexture != null)
                Object.DestroyImmediate(renderData.renderTexture, true);

            if (renderData.shaderData != null && renderData.shaderData.node != null)
                renderData.shaderData.node.UnregisterCallback(OnNodeModified);
        }

        void DestroyPreview(Identifier nodeId)
        {
            var renderData = Get(m_RenderDatas, nodeId);
            if (renderData != null)
            {
                // Check if we're destroying the shader data used by the master preview
                if (masterRenderData != null && masterRenderData.shaderData != null && masterRenderData.shaderData == renderData.shaderData)
                    masterRenderData.shaderData = m_RenderDatas.Where(x => x != null && x.shaderData.node is IMasterNode && x != renderData).Select(x => x.shaderData).FirstOrDefault();

                DestroyRenderData(renderData);

                Set(m_RenderDatas, nodeId, null);
                Set(m_Identifiers, nodeId, default(Identifier));
            }
        }

        void ReleaseUnmanagedResources()
        {
            if (m_ErrorTexture != null)
            {
                Object.DestroyImmediate(m_ErrorTexture);
                m_ErrorTexture = null;
            }
            if (m_SceneResources != null)
            {
                m_SceneResources.Dispose();
                m_SceneResources = null;
            }
            if (m_MasterRenderData != null)
                DestroyRenderData(m_MasterRenderData);
            foreach (var renderData in m_RenderDatas.Where(x => x != null))
                DestroyRenderData(renderData);
            m_RenderDatas.Clear();
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~PreviewManager()
        {
            throw new Exception("PreviewManager was not disposed of properly.");
        }

        const string k_EmptyShader = @"
Shader ""hidden/preview""
{
    SubShader
    {
        Tags { ""RenderType""=""Opaque"" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma    vertex    vert
            #pragma    fragment    frag

            #include    ""UnityCG.cginc""

            struct    appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }
}";

        T Get<T>(List<T> list, Identifier id)
        {
            var existingId = Get(m_Identifiers, id.index);
            if (existingId.valid && existingId.version != id.version)
                throw new Exception("Identifier version mismatch");
            return Get(list, id.index);
        }

        static T Get<T>(List<T> list, int index)
        {
            return index < list.Count ? list[index] : default(T);
        }

        void Set<T>(List<T> list, Identifier id, T value)
        {
            var existingId = Get(m_Identifiers, id.index);
            if (existingId.valid && existingId.version != id.version)
                throw new Exception("Identifier version mismatch");
            Set(list, id.index, value);
        }

        static void Set<T>(List<T> list, int index, T value)
        {
            // Make sure the list is large enough for the index
            for (var i = list.Count; i <= index; i++)
                list.Add(default(T));
            list[index] = value;
        }

        PreviewRenderData GetRenderData(Identifier id)
        {
            var value = Get(m_RenderDatas, id);
            if (value != null && value.shaderData.node.tempId.version != id.version)
                throw new Exception("Trying to access render data of a previous version of a node");
            return value;
        }
    }

    internal delegate void OnPreviewChanged();

    internal class PreviewShaderData
    {
        public INode node { get; set; }
        public Shader shader { get; set; }
        public Material mat { get; set; }
        public MaterialPropertyBlock props { get; set; }
        public string shaderString { get; set; }
        public bool isCompiling { get; set; }
        public bool hasError { get; set; }
    }

    internal class PreviewRenderData
    {
        public PreviewShaderData shaderData { get; set; }
        public RenderTexture renderTexture { get; set; }
        public Texture texture { get; set; }
        public PreviewMode previewMode { get; set; }
        public OnPreviewChanged onPreviewChanged;

        public void NotifyPreviewChanged()
        {
            if (onPreviewChanged != null)
                onPreviewChanged();
        }
    }
}

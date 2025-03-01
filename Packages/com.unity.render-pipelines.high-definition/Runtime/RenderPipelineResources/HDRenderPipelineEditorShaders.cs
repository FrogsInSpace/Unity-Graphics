#if UNITY_EDITOR
using System;
using System.ComponentModel;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    [HideInInspector]
    [Category("Resources/Editor Shaders")]
    [SupportedOnRenderPipeline(typeof(HDRenderPipelineAsset))]
    class HDRenderPipelineEditorShaders : IRenderPipelineResources
    {
        public int version => 0;

        #region Debug
        [Header("Debug")]
        [SerializeField]
        [ResourcePath("Runtime/Debug/GPUInlineDebugDrawer.shader")]
        private Shader m_GpuInlineDebugDrawerLine;

        public Shader gpuInlineDebugDrawerLine
        {
            get => m_GpuInlineDebugDrawerLine;
            set => this.SetValueAndNotify(ref m_GpuInlineDebugDrawerLine, value, nameof(m_GpuInlineDebugDrawerLine));
        }
        #endregion

        #region Autodesk
        [Header("Autodesk")]
        [SerializeField]
        [ResourcePath("Runtime/RenderPipelineResources/ShaderGraph/AutodeskInteractive.shadergraph")]
        private Shader m_AutodeskInteractive;

        public Shader autodeskInteractiveShader
        {
            get => m_AutodeskInteractive;
            set => this.SetValueAndNotify(ref m_AutodeskInteractive, value, nameof(m_AutodeskInteractive));
        }

        [SerializeField]
        [ResourcePath("Runtime/RenderPipelineResources/ShaderGraph/AutodeskInteractiveTransparent.shadergraph")]
        private Shader m_AutodeskInteractiveTransparent;

        public Shader autodeskInteractiveTransparentShader
        {
            get => m_AutodeskInteractiveTransparent;
            set => this.SetValueAndNotify(ref m_AutodeskInteractiveTransparent, value, nameof(m_AutodeskInteractiveTransparent));
        }

        [SerializeField]
        [ResourcePath("Runtime/RenderPipelineResources/ShaderGraph/AutodeskInteractiveMasked.shadergraph")]
        private Shader m_AutodeskInteractiveMasked;

        public Shader autodeskInteractiveMaskedShader
        {
            get => m_AutodeskInteractiveMasked;
            set => this.SetValueAndNotify(ref m_AutodeskInteractiveMasked, value, nameof(m_AutodeskInteractiveMasked));
        }
        #endregion

        #region SpeedTree
        [Header("SpeedTree")]
        [SerializeField]
        [ResourcePath("Runtime/Material/Nature/SpeedTree8.shadergraph")]
        private Shader m_DefaultSpeedTree8Shader;

        public Shader defaultSpeedTree8Shader
        {
            get => m_DefaultSpeedTree8Shader;
            set => this.SetValueAndNotify(ref m_DefaultSpeedTree8Shader, value, nameof(m_DefaultSpeedTree8Shader));
        }
        #endregion
    }
}
#endif

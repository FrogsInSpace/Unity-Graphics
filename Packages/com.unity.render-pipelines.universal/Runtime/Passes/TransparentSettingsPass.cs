using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Applies relevant settings before rendering transparent objects
    /// </summary>

    internal class TransparentSettingsPass : ScriptableRenderPass
    {
        bool m_shouldReceiveShadows;

        const string m_ProfilerTag = "Transparent Settings Pass";
        private static readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

        public TransparentSettingsPass(RenderPassEvent evt, bool shadowReceiveSupported)
        {
            base.profilingSampler = new ProfilingSampler(nameof(TransparentSettingsPass));
            renderPassEvent = evt;
            m_shouldReceiveShadows = shadowReceiveSupported;
        }

        public bool Setup()
        {
            // Currently we only need to enqueue this pass when the user
            // doesn't want transparent objects to receive shadows
            return !m_shouldReceiveShadows;
        }

        [Obsolete(DeprecationMessage.CompatibilityScriptingAPIObsolete, false)]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Get a command buffer...
            var cmd = renderingData.commandBuffer;
            ExecutePass(CommandBufferHelpers.GetRasterCommandBuffer(cmd), m_shouldReceiveShadows);
        }

        public static void ExecutePass(RasterCommandBuffer cmd, bool shouldReceiveShadows)
        {
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                // Toggle light shadows enabled based on the renderer setting set in the constructor
                cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadows, shouldReceiveShadows);
                cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadowCascades, shouldReceiveShadows);
                cmd.SetKeyword(ShaderGlobalKeywords.AdditionalLightShadows, shouldReceiveShadows);
            }
        }
    }
}

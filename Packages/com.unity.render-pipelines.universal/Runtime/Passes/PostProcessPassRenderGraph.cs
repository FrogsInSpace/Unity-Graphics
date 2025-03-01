using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using System;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    internal partial class PostProcessPass : ScriptableRenderPass
    {
        static readonly int s_CameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int s_CameraOpaqueTextureID = Shader.PropertyToID("_CameraOpaqueTexture");

        private class UpdateCameraResolutionPassData
        {
            internal Vector2Int newCameraTargetSize;
        }

        // Updates render target descriptors and shader constants to reflect a new render size
        // This should be called immediately after the resolution changes mid-frame (typically after an upscaling operation).
        void UpdateCameraResolution(RenderGraph renderGraph, UniversalCameraData cameraData, Vector2Int newCameraTargetSize)
        {
            // Update the local descriptor and the camera data descriptor to reflect post-upscaled sizes
            m_Descriptor.width = newCameraTargetSize.x;
            m_Descriptor.height = newCameraTargetSize.y;
            cameraData.cameraTargetDescriptor.width = newCameraTargetSize.x;
            cameraData.cameraTargetDescriptor.height = newCameraTargetSize.y;

            // Update the shader constants to reflect the new camera resolution
            using (var builder = renderGraph.AddUnsafePass<UpdateCameraResolutionPassData>("Update Camera Resolution", out var passData))
            {
                passData.newCameraTargetSize = newCameraTargetSize;

                // This pass only modifies shader constants so we need to set some special flags to ensure it isn't culled or optimized away
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (UpdateCameraResolutionPassData data, UnsafeGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalVector(
                        ShaderPropertyId.screenSize,
                        new Vector4(
                            data.newCameraTargetSize.x,
                            data.newCameraTargetSize.y,
                            1.0f / data.newCameraTargetSize.x,
                            1.0f / data.newCameraTargetSize.y
                        )
                    );
                });
            }
        }

        #region StopNaNs
        private class StopNaNsPassData
        {
            internal TextureHandle stopNaNTarget;
            internal TextureHandle sourceTexture;
            internal Material stopNaN;
        }

        public void RenderStopNaN(RenderGraph renderGraph, RenderTextureDescriptor cameraTargetDescriptor, in TextureHandle activeCameraColor, out TextureHandle stopNaNTarget)
        {
            var desc = PostProcessPass.GetCompatibleDescriptor(cameraTargetDescriptor,
                cameraTargetDescriptor.width,
                cameraTargetDescriptor.height,
                cameraTargetDescriptor.graphicsFormat,
                DepthBits.None);

            stopNaNTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_StopNaNsTarget", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddRasterRenderPass<StopNaNsPassData>("Stop NaNs", out var passData,
                       ProfilingSampler.Get(URPProfileId.RG_StopNaNs)))
            {
                passData.stopNaNTarget = stopNaNTarget;
                builder.SetRenderAttachment(stopNaNTarget, 0, AccessFlags.ReadWrite);
                passData.sourceTexture = activeCameraColor;
                builder.UseTexture(activeCameraColor, AccessFlags.Read);
                passData.stopNaN = m_Materials.stopNaN;
                builder.SetRenderFunc((StopNaNsPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;
                    Vector2 viewportScale = sourceTextureHdl.useScaling? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, data.stopNaN, 0);
                });
            }
        }
        #endregion

        #region SMAA
        private class SMAASetupPassData
        {
            internal Vector4 metrics;
            internal Texture2D areaTexture;
            internal Texture2D searchTexture;
            internal float stencilRef;
            internal float stencilMask;
            internal AntialiasingQuality antialiasingQuality;
            internal Material material;
        }

        private class SMAAPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal TextureHandle depthStencilTexture;
            internal TextureHandle blendTexture;
            internal Material material;
        }

        public void RenderSMAA(RenderGraph renderGraph, UniversalResourceData resourceData, AntialiasingQuality antialiasingQuality, in TextureHandle source, out TextureHandle SMAATarget)
        {

            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);
            SMAATarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_SMAATarget", true, FilterMode.Bilinear);

            var edgeTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_SMAAEdgeFormat,
                DepthBits.None);
            var edgeTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, edgeTextureDesc, "_EdgeStencilTexture", true, FilterMode.Bilinear);

            var edgeTextureStencilDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                GraphicsFormat.None,
                DepthBits.Depth24);
            var edgeTextureStencil = UniversalRenderer.CreateRenderGraphTexture(renderGraph, edgeTextureStencilDesc, "_EdgeTexture", true, FilterMode.Bilinear);

            var blendTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                GraphicsFormat.R8G8B8A8_UNorm,
                DepthBits.None);
            var blendTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, blendTextureDesc, "_BlendTexture", true, FilterMode.Point);

            // Anti-aliasing
            var material = m_Materials.subpixelMorphologicalAntialiasing;

            using (var builder = renderGraph.AddRasterRenderPass<SMAASetupPassData>("SMAA Material Setup", out var passData, ProfilingSampler.Get(URPProfileId.RG_SMAAMaterialSetup)))
            {
                const int kStencilBit = 64;
                // TODO RENDERGRAPH: handle dynamic scaling
                passData.metrics = new Vector4(1f / m_Descriptor.width, 1f / m_Descriptor.height, m_Descriptor.width, m_Descriptor.height);
                passData.areaTexture = m_Data.textures.smaaAreaTex;
                passData.searchTexture = m_Data.textures.smaaSearchTex;
                passData.stencilRef = (float)kStencilBit;
                passData.stencilMask = (float)kStencilBit;
                passData.antialiasingQuality = antialiasingQuality;
                passData.material = material;

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((SMAASetupPassData data, RasterGraphContext context) =>
                {
                    // Globals
                    data.material.SetVector(ShaderConstants._Metrics, data.metrics);
                    data.material.SetTexture(ShaderConstants._AreaTexture, data.areaTexture);
                    data.material.SetTexture(ShaderConstants._SearchTexture, data.searchTexture);
                    data.material.SetFloat(ShaderConstants._StencilRef, data.stencilRef);
                    data.material.SetFloat(ShaderConstants._StencilMask, data.stencilMask);

                    // Quality presets
                    data.material.shaderKeywords = null;

                    switch (data.antialiasingQuality)
                    {
                        case AntialiasingQuality.Low:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaLow);
                            break;
                        case AntialiasingQuality.Medium:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaMedium);
                            break;
                        case AntialiasingQuality.High:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaHigh);
                            break;
                    }
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<SMAAPassData>("SMAA Edge Detection", out var passData, ProfilingSampler.Get(URPProfileId.RG_SMAAEdgeDetection)))
            {
                passData.destinationTexture = edgeTexture;
                builder.SetRenderAttachment(edgeTexture, 0, AccessFlags.Write);
                passData.depthStencilTexture = edgeTextureStencil;
                builder.SetRenderAttachmentDepth(edgeTextureStencil, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraDepth ,AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((SMAAPassData data, RasterGraphContext context) =>
                {
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 1: Edge detection
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<SMAAPassData>("SMAA Blend weights", out var passData, ProfilingSampler.Get(URPProfileId.RG_SMAABlendWeight)))
            {
                passData.destinationTexture = blendTexture;
                builder.SetRenderAttachment(blendTexture, 0, AccessFlags.Write);
                passData.depthStencilTexture = edgeTextureStencil;
                builder.SetRenderAttachmentDepth(edgeTextureStencil, AccessFlags.Read);
                passData.sourceTexture = edgeTexture;
                builder.UseTexture(edgeTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((SMAAPassData data, RasterGraphContext context) =>
                {
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 2: Blend weights
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 1);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<SMAAPassData>("SMAA Neighborhood blending", out var passData, ProfilingSampler.Get(URPProfileId.RG_SMAANeighborhoodBlend)))
            {
                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = SMAATarget;
                builder.SetRenderAttachment(SMAATarget, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.blendTexture = blendTexture;
                builder.UseTexture(blendTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((SMAAPassData data, RasterGraphContext context) =>
                {
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 3: Neighborhood blending
                    SMAAMaterial.SetTexture(ShaderConstants._BlendTexture, data.blendTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 2);
                });
            }
        }
        #endregion

        #region Bloom
        private class UberSetupBloomPassData
        {
            internal Vector4 bloomParams;
            internal bool useRGBM;
            internal Vector4 dirtScaleOffset;
            internal float dirtIntensity;
            internal Texture dirtTexture;
            internal bool highQualityFilteringValue;
            internal TextureHandle bloomTexture;
            internal Material uberMaterial;
        }

        public void UberPostSetupBloomPass(RenderGraph rendergraph, in TextureHandle bloomTexture, Material uberMaterial)
        {
            using (var builder = rendergraph.AddRasterRenderPass<UberSetupBloomPassData>("UberPost - UberPostSetupBloomPass", out var passData, ProfilingSampler.Get(URPProfileId.RG_UberPostSetupBloomPass)))
            {
                // Setup bloom on uber
                var tint = m_Bloom.tint.value.linear;
                var luma = ColorUtils.Luminance(tint);
                tint = luma > 0f ? tint * (1f / luma) : Color.white;
                var bloomParams = new Vector4(m_Bloom.intensity.value, tint.r, tint.g, tint.b);

                // Setup lens dirtiness on uber
                // Keep the aspect ratio correct & center the dirt texture, we don't want it to be
                // stretched or squashed
                var dirtTexture = m_Bloom.dirtTexture.value == null ? Texture2D.blackTexture : m_Bloom.dirtTexture.value;
                float dirtRatio = dirtTexture.width / (float)dirtTexture.height;
                float screenRatio = m_Descriptor.width / (float)m_Descriptor.height;
                var dirtScaleOffset = new Vector4(1f, 1f, 0f, 0f);
                float dirtIntensity = m_Bloom.dirtIntensity.value;

                if (dirtRatio > screenRatio)
                {
                    dirtScaleOffset.x = screenRatio / dirtRatio;
                    dirtScaleOffset.z = (1f - dirtScaleOffset.x) * 0.5f;
                }
                else if (screenRatio > dirtRatio)
                {
                    dirtScaleOffset.y = dirtRatio / screenRatio;
                    dirtScaleOffset.w = (1f - dirtScaleOffset.y) * 0.5f;
                }

                passData.bloomParams = bloomParams;
                passData.dirtScaleOffset = dirtScaleOffset;
                passData.dirtIntensity = dirtIntensity;
                passData.dirtTexture = dirtTexture;
                passData.highQualityFilteringValue = m_Bloom.highQualityFiltering.value;

                passData.bloomTexture = bloomTexture;
                builder.UseTexture(bloomTexture, AccessFlags.Read);
                passData.uberMaterial = uberMaterial;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((UberSetupBloomPassData data, RasterGraphContext context) =>
                {
                    var uberMaterial = data.uberMaterial;
                    uberMaterial.SetVector(ShaderConstants._Bloom_Params, data.bloomParams);
                    uberMaterial.SetFloat(ShaderConstants._Bloom_RGBM, data.useRGBM ? 1f : 0f);
                    uberMaterial.SetVector(ShaderConstants._LensDirt_Params, data.dirtScaleOffset);
                    uberMaterial.SetFloat(ShaderConstants._LensDirt_Intensity, data.dirtIntensity);
                    uberMaterial.SetTexture(ShaderConstants._LensDirt_Texture, data.dirtTexture);

                    // Keyword setup - a bit convoluted as we're trying to save some variants in Uber...
                    if (data.highQualityFilteringValue)
                        uberMaterial.EnableKeyword(data.dirtIntensity > 0f ? ShaderKeywordStrings.BloomHQDirt : ShaderKeywordStrings.BloomHQ);
                    else
                        uberMaterial.EnableKeyword(data.dirtIntensity > 0f ? ShaderKeywordStrings.BloomLQDirt : ShaderKeywordStrings.BloomLQ);

                    uberMaterial.SetTexture(ShaderConstants._Bloom_Texture, data.bloomTexture);
                });
            }
        }

        private class BloomPassData
        {
            internal int mipCount;

            internal Material material;
            internal Material[] upsampleMaterials;

            internal TextureHandle sourceTexture;

            internal TextureHandle[] bloomMipUp;
            internal TextureHandle[] bloomMipDown;
        }

        internal struct BloomMaterialParams
        {
            internal Vector4 parameters;
            internal bool highQualityFiltering;
            internal bool useRGBM;
            internal bool Equals(ref BloomMaterialParams other)
            {
                return parameters == other.parameters && highQualityFiltering == other.highQualityFiltering && useRGBM == other.useRGBM;
            }
        }

        public void RenderBloomTexture(RenderGraph renderGraph, in TextureHandle source, out TextureHandle destination)
        {
            // Start at half-res
            int downres = 1;
            switch (m_Bloom.downscale.value)
            {
                case BloomDownscaleMode.Half:
                    downres = 1;
                    break;
                case BloomDownscaleMode.Quarter:
                    downres = 2;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            int tw = m_Descriptor.width >> downres;
            int th = m_Descriptor.height >> downres;

            // Determine the iteration count
            int maxSize = Mathf.Max(tw, th);
            int iterations = Mathf.FloorToInt(Mathf.Log(maxSize, 2f) - 1);
            int mipCount = Mathf.Clamp(iterations, 1, m_Bloom.maxIterations.value);

            // Setup
            using(new ProfilingScope(ProfilingSampler.Get(URPProfileId.RG_BloomSetup)))
            {
                // Pre-filtering parameters
                float clamp = m_Bloom.clamp.value;
                float threshold = Mathf.GammaToLinearSpace(m_Bloom.threshold.value);
                float thresholdKnee = threshold * 0.5f; // Hardcoded soft knee

                // Material setup
                float scatter = Mathf.Lerp(0.05f, 0.95f, m_Bloom.scatter.value);

                BloomMaterialParams bloomParams = new BloomMaterialParams();
                bloomParams.parameters = new Vector4(scatter, clamp, threshold, thresholdKnee);
                bloomParams.highQualityFiltering = m_Bloom.highQualityFiltering.value;
                bloomParams.useRGBM = m_UseRGBM;

                // Setting keywords can be somewhat expensive on low-end platforms.
                // Previous params are cached to avoid setting the same keywords every frame.
                var material = m_Materials.bloom;
                bool bloomParamsDirty = !m_BloomParamsPrev.Equals(ref bloomParams);
                bool isParamsPropertySet = material.HasProperty(ShaderConstants._Params);
                if (bloomParamsDirty || !isParamsPropertySet)
                {
                    material.SetVector(ShaderConstants._Params, bloomParams.parameters);
                    CoreUtils.SetKeyword(material, ShaderKeywordStrings.BloomHQ, bloomParams.highQualityFiltering);
                    CoreUtils.SetKeyword(material, ShaderKeywordStrings.UseRGBM, bloomParams.useRGBM);

                    // These materials are duplicate just to allow different bloom blits to use different textures.
                    for (uint i = 0; i < k_MaxPyramidSize; ++i)
                    {
                        var materialPyramid = m_Materials.bloomUpsample[i];
                        materialPyramid.SetVector(ShaderConstants._Params, bloomParams.parameters);
                        CoreUtils.SetKeyword(materialPyramid, ShaderKeywordStrings.BloomHQ, bloomParams.highQualityFiltering);
                        CoreUtils.SetKeyword(materialPyramid, ShaderKeywordStrings.UseRGBM, bloomParams.useRGBM);
                    }

                    m_BloomParamsPrev = bloomParams;
                }

                // Create bloom mip pyramid textures
                {
                    var desc = GetCompatibleDescriptor(tw, th, m_DefaultHDRFormat);
                    _BloomMipDown[0] = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, m_BloomMipDown[0].name, false, FilterMode.Bilinear);
                    _BloomMipUp[0] = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, m_BloomMipUp[0].name, false, FilterMode.Bilinear);

                    for (int i = 1; i < mipCount; i++)
                    {
                        tw = Mathf.Max(1, tw >> 1);
                        th = Mathf.Max(1, th >> 1);
                        ref TextureHandle mipDown = ref _BloomMipDown[i];
                        ref TextureHandle mipUp = ref _BloomMipUp[i];

                        desc.width = tw;
                        desc.height = th;

                        // NOTE: Reuse RTHandle names for TextureHandles
                        mipDown = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, m_BloomMipDown[i].name, false, FilterMode.Bilinear);
                        mipUp = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, m_BloomMipUp[i].name, false, FilterMode.Bilinear);
                    }
                }
            }

            using (var builder = renderGraph.AddUnsafePass<BloomPassData>("Bloom", out var passData, ProfilingSampler.Get(URPProfileId.Bloom)))
            {
                passData.mipCount = mipCount;
                passData.material = m_Materials.bloom;
                passData.upsampleMaterials = m_Materials.bloomUpsample;
                passData.sourceTexture = source;
                passData.bloomMipDown = _BloomMipDown;
                passData.bloomMipUp = _BloomMipUp;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.UseTexture(source, AccessFlags.Read);
                for (int i = 0; i < mipCount; i++)
                {
                    builder.UseTexture(_BloomMipDown[i], AccessFlags.ReadWrite);
                    builder.UseTexture(_BloomMipUp[i], AccessFlags.ReadWrite);
                }

                builder.SetRenderFunc(static (BloomPassData data, UnsafeGraphContext context) =>
                {
                    // TODO: can't call BlitTexture with unsafe command buffer
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    var material = data.material;
                    int mipCount = data.mipCount;

                    var loadAction = RenderBufferLoadAction.DontCare;   // Blit - always write all pixels
                    var storeAction = RenderBufferStoreAction.Store;    // Blit - always read by then next Blit

                    // Prefilter
                    using(new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.RG_BloomPrefilter)))
                    {
                        Blitter.BlitCameraTexture(cmd, data.sourceTexture, data.bloomMipDown[0], loadAction, storeAction, material, 0);
                    }

                    // Downsample - gaussian pyramid
                    // Classic two pass gaussian blur - use mipUp as a temporary target
                    //   First pass does 2x downsampling + 9-tap gaussian
                    //   Second pass does 9-tap gaussian using a 5-tap filter + bilinear filtering
                    using(new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.RG_BloomDownsample)))
                    {
                        TextureHandle lastDown = data.bloomMipDown[0];
                        for (int i = 1; i < mipCount; i++)
                        {
                            TextureHandle mipDown = data.bloomMipDown[i];
                            TextureHandle mipUp = data.bloomMipUp[i];

                            Blitter.BlitCameraTexture(cmd, lastDown, mipUp, loadAction, storeAction, material, 1);
                            Blitter.BlitCameraTexture(cmd, mipUp, mipDown, loadAction, storeAction, material, 2);

                            lastDown = mipDown;
                        }
                    }

                    using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.RG_BloomUpsample)))
                    {
                        // Upsample (bilinear by default, HQ filtering does bicubic instead
                        for (int i = mipCount - 2; i >= 0; i--)
                        {
                            TextureHandle lowMip = (i == mipCount - 2) ? data.bloomMipDown[i + 1] : data.bloomMipUp[i + 1];
                            TextureHandle highMip = data.bloomMipDown[i];
                            TextureHandle dst = data.bloomMipUp[i];

                            // We need a separate material for each upsample pass because setting the low texture mip source
                            // gets overriden by the time the render func is executed.
                            // Material is a reference, so all the blits would share the same material state in the cmdbuf.
                            // NOTE: another option would be to use cmd.SetGlobalTexture().
                            var upMaterial = data.upsampleMaterials[i];
                            upMaterial.SetTexture(ShaderConstants._SourceTexLowMip, lowMip);

                            Blitter.BlitCameraTexture(cmd, highMip, dst, loadAction, storeAction, upMaterial, 3);
                        }
                    }
                });

                destination = passData.bloomMipUp[0];
            }
        }
        #endregion

        #region DoF
        public void RenderDoF(RenderGraph renderGraph, UniversalResourceData resourceData, in TextureHandle source, out TextureHandle destination)
        {
            var dofMaterial = m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian ? m_Materials.gaussianDepthOfField : m_Materials.bokehDepthOfField;

            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);
            destination = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_DoFTarget", true, FilterMode.Bilinear);

            if (m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian)
            {
                RenderDoFGaussian(renderGraph, resourceData, source, destination, ref dofMaterial);
            }
            else if (m_DepthOfField.mode.value == DepthOfFieldMode.Bokeh)
            {
                RenderDoFBokeh(renderGraph, resourceData, source, destination, ref dofMaterial);
            }
        }

        private class DoFGaussianSetupPassData
        {
            internal TextureHandle source;
            internal int downSample;
            internal RenderingData renderingData;
            internal Vector3 cocParams;
            internal bool highQualitySamplingValue;
            internal Material material;
        };

        private class DoFGaussianPassData
        {
            internal TextureHandle cocTexture;
            internal TextureHandle colorTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
        };

        public void RenderDoFGaussian(RenderGraph renderGraph, UniversalResourceData resourceData, in TextureHandle source, in TextureHandle destination, ref Material dofMaterial)
        {
            int downSample = 2;
            var material = dofMaterial;
            int wh = m_Descriptor.width / downSample;
            int hh = m_Descriptor.height / downSample;

            using (var builder = renderGraph.AddRasterRenderPass<DoFGaussianSetupPassData>("Setup DoF passes", out var passData, ProfilingSampler.Get(URPProfileId.RG_SetupDoF)))
            {
                float farStart = m_DepthOfField.gaussianStart.value;
                float farEnd = Mathf.Max(farStart, m_DepthOfField.gaussianEnd.value);

                // Assumes a radius of 1 is 1 at 1080p
                // Past a certain radius our gaussian kernel will look very bad so we'll clamp it for
                // very high resolutions (4K+).
                float maxRadius = m_DepthOfField.gaussianMaxRadius.value * (wh / 1080f);
                maxRadius = Mathf.Min(maxRadius, 2f);

                passData.source = source;
                passData.downSample = downSample;
                passData.cocParams = new Vector3(farStart, farEnd, maxRadius);
                passData.highQualitySamplingValue = m_DepthOfField.highQualitySampling.value;
                passData.material = material;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((DoFGaussianSetupPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var dofmaterial = data.material;

                    dofmaterial.SetVector(ShaderConstants._CoCParams, data.cocParams);
                    CoreUtils.SetKeyword(dofmaterial, ShaderKeywordStrings.HighQualitySampling, data.highQualitySamplingValue);
                    PostProcessUtils.SetSourceSize(cmd, data.source);
                    cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(1.0f / data.downSample, 1.0f / data.downSample, data.downSample, data.downSample));
                });
            }

            // Temporary textures
            var fullCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, m_Descriptor.width, m_Descriptor.height, m_GaussianCoCFormat);
            var fullCoCTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, fullCoCTextureDesc, "_FullCoCTexture", true, FilterMode.Bilinear);
            var halfCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, m_GaussianCoCFormat);
            var halfCoCTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, halfCoCTextureDesc, "_HalfCoCTexture", true, FilterMode.Bilinear);
            var pingTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, m_DefaultHDRFormat);
            var pingTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, pingTextureDesc, "_PingTexture", true, FilterMode.Bilinear);
            var pongTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, m_DefaultHDRFormat);
            var pongTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, pongTextureDesc, "_PongTexture", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddRasterRenderPass<DoFGaussianPassData>("Depth of Field - Compute CoC", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFComputeCOC)))
            {
                builder.SetRenderAttachment(fullCoCTexture, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);

                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);

                passData.material = material;
                builder.SetRenderFunc((DoFGaussianPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;
                    // Compute CoC
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<DoFGaussianPassData>("Depth of Field - Downscale & Prefilter Color + CoC", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFDownscalePrefilter)))
            {
                builder.SetRenderAttachment(halfCoCTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachment(pingTexture, 1, AccessFlags.Write);
                // TODO RENDERGRAPH: Setting MRTs without a depth buffer is not supported in the old path, could we add the support and remove the depth?
                // Should go away if the old path goes away
                if (!renderGraph.nativeRenderPassesEnabled)
                    builder.SetRenderAttachmentDepth(renderGraph.CreateTexture(halfCoCTexture), AccessFlags.ReadWrite);
                builder.AllowGlobalStateModification(true);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.cocTexture = fullCoCTexture;
                builder.UseTexture(fullCoCTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((DoFGaussianPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Downscale & prefilter color + coc
                    dofmaterial.SetTexture(ShaderConstants._FullCoCTexture, data.cocTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 1);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<DoFGaussianPassData>("Depth of Field - Blur H", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFBlurH)))
            {
                builder.SetRenderAttachment(pongTexture, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                passData.sourceTexture = pingTexture;
                builder.UseTexture(pingTexture, AccessFlags.Read);
                passData.cocTexture = halfCoCTexture;
                builder.UseTexture(halfCoCTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((DoFGaussianPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTexture = data.sourceTexture;

                    // Blur
                    dofmaterial.SetTexture(ShaderConstants._HalfCoCTexture, data.cocTexture);
                    Vector2 viewportScale = sourceTexture.useScaling ? new Vector2(sourceTexture.rtHandleProperties.rtHandleScale.x, sourceTexture.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTexture, viewportScale, dofmaterial, 2);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<DoFGaussianPassData>("Depth of Field - Blur V", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFBlurV)))
            {
                builder.SetRenderAttachment(pingTexture, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                passData.sourceTexture = pongTexture;
                builder.UseTexture(pongTexture, AccessFlags.Read);
                passData.cocTexture = halfCoCTexture;
                builder.UseTexture(halfCoCTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((DoFGaussianPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Blur
                    dofmaterial.SetTexture(ShaderConstants._HalfCoCTexture, data.cocTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 3);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<DoFGaussianPassData>("Depth of Field - Composite", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFComposite)))
            {
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.cocTexture = fullCoCTexture;
                builder.UseTexture(fullCoCTexture, AccessFlags.Read);
                passData.colorTexture = pingTexture;
                builder.UseTexture(pingTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((DoFGaussianPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Composite
                    dofmaterial.SetTexture(ShaderConstants._ColorTexture, data.colorTexture);
                    dofmaterial.SetTexture(ShaderConstants._FullCoCTexture, data.cocTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 4);
                });
            }
        }

        private class DoFBokehSetupPassData
        {
            internal Vector4[] bokehKernel;
            internal TextureHandle source;
            internal int downSample;
            internal float uvMargin;
            internal Vector4 cocParams;
            internal bool useFastSRGBLinearConversion;
            internal Material material;
        };

        private class DoFBokehPassData
        {
            internal TextureHandle cocTexture;
            internal TextureHandle dofTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
        };

        public void RenderDoFBokeh(RenderGraph renderGraph, UniversalResourceData resourceData, in TextureHandle source, in TextureHandle destination, ref Material dofMaterial)
        {
            int downSample = 2;
            var material = dofMaterial;
            int wh = m_Descriptor.width / downSample;
            int hh = m_Descriptor.height / downSample;

            using (var builder = renderGraph.AddRasterRenderPass<DoFBokehSetupPassData>("Setup DoF passes", out var passData, ProfilingSampler.Get(URPProfileId.RG_SetupDoF)))
            {
                // "A Lens and Aperture Camera Model for Synthetic Image Generation" [Potmesil81]
                float F = m_DepthOfField.focalLength.value / 1000f;
                float A = m_DepthOfField.focalLength.value / m_DepthOfField.aperture.value;
                float P = m_DepthOfField.focusDistance.value;
                float maxCoC = (A * F) / (P - F);
                float maxRadius = GetMaxBokehRadiusInPixels(m_Descriptor.height);
                float rcpAspect = 1f / (wh / (float)hh);


                // Prepare the bokeh kernel constant buffer
                int hash = m_DepthOfField.GetHashCode();
                if (hash != m_BokehHash || maxRadius != m_BokehMaxRadius || rcpAspect != m_BokehRCPAspect)
                {
                    m_BokehHash = hash;
                    m_BokehMaxRadius = maxRadius;
                    m_BokehRCPAspect = rcpAspect;
                    PrepareBokehKernel(maxRadius, rcpAspect);
                }
                float uvMargin = (1.0f / m_Descriptor.height) * downSample;

                passData.bokehKernel = m_BokehKernel;
                passData.source = source;
                passData.downSample = downSample;
                passData.uvMargin = uvMargin;
                passData.cocParams = new Vector4(P, maxCoC, maxRadius, rcpAspect);
                passData.useFastSRGBLinearConversion = m_UseFastSRGBLinearConversion;
                passData.material = material;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((DoFBokehSetupPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;

                    CoreUtils.SetKeyword(dofmaterial, ShaderKeywordStrings.UseFastSRGBLinearConversion, data.useFastSRGBLinearConversion);
                    cmd.SetGlobalVector(ShaderConstants._CoCParams, data.cocParams);
                    cmd.SetGlobalVectorArray(ShaderConstants._BokehKernel, data.bokehKernel);
                    cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(1.0f / data.downSample, 1.0f / data.downSample, data.downSample, data.downSample));
                    cmd.SetGlobalVector(ShaderConstants._BokehConstants, new Vector4(data.uvMargin, data.uvMargin * 2.0f));
                    PostProcessUtils.SetSourceSize(cmd, data.source);
                });
            }

            // Temporary textures
            var fullCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, m_Descriptor.width, m_Descriptor.height, GraphicsFormat.R8_UNorm);
            var fullCoCTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, fullCoCTextureDesc, "_FullCoCTexture", true, FilterMode.Bilinear);
            var pingTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, GraphicsFormat.R16G16B16A16_SFloat);
            var pingTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, pingTextureDesc, "_PingTexture", true, FilterMode.Bilinear);
            var pongTextureDesc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor, wh, hh, GraphicsFormat.R16G16B16A16_SFloat);
            var pongTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, pongTextureDesc, "_PongTexture", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddRasterRenderPass<DoFBokehPassData>("Depth of Field - Compute CoC", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFComputeCOC)))
            {
                builder.SetRenderAttachment(fullCoCTexture, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.material = material;

                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);

                builder.SetRenderFunc((DoFBokehPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Compute CoC
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<DoFBokehPassData>("Depth of Field - Downscale & Prefilter Color + CoC", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFDownscalePrefilter)))
            {
                builder.SetRenderAttachment(pingTexture, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.cocTexture = fullCoCTexture;
                builder.UseTexture(fullCoCTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((DoFBokehPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Downscale & prefilter color + coc
                    dofmaterial.SetTexture(ShaderConstants._FullCoCTexture, data.cocTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 1);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<DoFBokehPassData>("Depth of Field - Bokeh Blur", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFBlurBokeh)))
            {
                builder.SetRenderAttachment(pongTexture, 0, AccessFlags.Write);
                passData.sourceTexture = pingTexture;
                builder.UseTexture(pingTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((DoFBokehPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Downscale & prefilter color + coc
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 2);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<DoFBokehPassData>("Depth of Field - Post-filtering", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFPostFilter)))
            {
                builder.SetRenderAttachment(pingTexture, 0, AccessFlags.Write);
                passData.sourceTexture = pongTexture;
                builder.UseTexture(pongTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((DoFBokehPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Post - filtering
                    // TODO RENDERGRAPH: Look into loadstore op in BlitDstDiscardContent
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 3);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<DoFBokehPassData>("Depth of Field - Composite", out var passData, ProfilingSampler.Get(URPProfileId.RG_DOFComposite)))
            {
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.dofTexture = pingTexture;
                builder.UseTexture(pingTexture, AccessFlags.Read);
                builder.UseTexture(fullCoCTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc((DoFBokehPassData data, RasterGraphContext context) =>
                {
                    var dofmaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Composite
                    // TODO RENDERGRAPH: Look into loadstore op in BlitDstDiscardContent
                    dofmaterial.SetTexture(ShaderConstants._DofTexture, data.dofTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, dofmaterial, 4);
                });
            }
        }
        #endregion

        #region Panini
        private class PaniniProjectionPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal RenderTextureDescriptor sourceTextureDesc;
            internal Material material;
            internal Vector4 paniniParams;
            internal bool isPaniniGeneric;
        }

        public void RenderPaniniProjection(RenderGraph renderGraph, Camera camera, in TextureHandle source, out TextureHandle destination)
        {
            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);

            destination = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_PaniniProjectionTarget", true, FilterMode.Bilinear);

            float distance = m_PaniniProjection.distance.value;
            var viewExtents = CalcViewExtents(camera);
            var cropExtents = CalcCropExtents(camera, distance);

            float scaleX = cropExtents.x / viewExtents.x;
            float scaleY = cropExtents.y / viewExtents.y;
            float scaleF = Mathf.Min(scaleX, scaleY);

            float paniniD = distance;
            float paniniS = Mathf.Lerp(1f, Mathf.Clamp01(scaleF), m_PaniniProjection.cropToFit.value);

            using (var builder = renderGraph.AddRasterRenderPass<PaniniProjectionPassData>("Panini Projection", out var passData, ProfilingSampler.Get(URPProfileId.PaniniProjection)))
            {
                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destination;
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.material = m_Materials.paniniProjection;
                passData.paniniParams = new Vector4(viewExtents.x, viewExtents.y, paniniD, paniniS);
                passData.isPaniniGeneric = 1f - Mathf.Abs(paniniD) > float.Epsilon;
                passData.sourceTextureDesc = m_Descriptor;

                builder.SetRenderFunc((PaniniProjectionPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    cmd.SetGlobalVector(ShaderConstants._Params, data.paniniParams);
                    data.material.EnableKeyword(data.isPaniniGeneric ? ShaderKeywordStrings.PaniniGeneric : ShaderKeywordStrings.PaniniUnitDistance);

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, data.material, 0);
                });

                return;
            }
        }
        #endregion

        #region TemporalAA

        private const string _TemporalAATargetName = "_TemporalAATarget";
        private void RenderTemporalAA(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData, ref TextureHandle source, out TextureHandle destination)
        {
            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);
            destination = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, _TemporalAATargetName, false, FilterMode.Bilinear);

            TextureHandle cameraDepth = resourceData.cameraDepth;
            TextureHandle motionVectors = resourceData.motionVectorColor;

            Debug.Assert(motionVectors.IsValid(), "MotionVectors are invalid. TAA requires a motion vector texture.");

            TemporalAA.Render(renderGraph, m_Materials.temporalAntialiasing, cameraData, ref source, ref cameraDepth, ref motionVectors, ref destination);
        }
        #endregion

        #region STP

        private const string _UpscaledColorTargetName = "_UpscaledColorTarget";

        private void RenderSTP(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData, ref TextureHandle source, out TextureHandle destination)
        {
            TextureHandle cameraDepth = resourceData.cameraDepth;
            TextureHandle motionVectors = resourceData.motionVectorColor;

            Debug.Assert(motionVectors.IsValid(), "MotionVectors are invalid. STP requires a motion vector texture.");

            var desc = GetCompatibleDescriptor(cameraData.cameraTargetDescriptor,
                cameraData.pixelWidth,
                cameraData.pixelHeight,
                cameraData.cameraTargetDescriptor.graphicsFormat,
                DepthBits.None);

            // STP uses compute shaders so all render textures must enable random writes
            desc.enableRandomWrite = true;

            // Avoid enabling sRGB because STP works with compute shaders which can't output sRGB automatically.
            desc.sRGB = false;

            destination = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, _UpscaledColorTargetName, false, FilterMode.Bilinear);

            int frameIndex = Time.frameCount;
            var noiseTexture = m_Data.textures.blueNoise16LTex[frameIndex & (m_Data.textures.blueNoise16LTex.Length - 1)];

            StpUtils.Execute(renderGraph, resourceData, cameraData, source, cameraDepth, motionVectors, destination, noiseTexture);

            // Update the camera resolution to reflect the upscaled size
            UpdateCameraResolution(renderGraph, cameraData, new Vector2Int(desc.width, desc.height));
        }
        #endregion

        #region MotionBlur
        private class MotionBlurPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal TextureHandle motionVectors;
            internal Material material;
            internal int passIndex;
            internal Camera camera;
            internal XRPass xr;
            internal float intensity;
            internal float clamp;
        }

        public void RenderMotionBlur(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData, in TextureHandle source, out TextureHandle destination)
        {
            var material = m_Materials.cameraMotionBlur;
            var desc = PostProcessPass.GetCompatibleDescriptor(m_Descriptor,
                m_Descriptor.width,
                m_Descriptor.height,
                m_Descriptor.graphicsFormat,
                DepthBits.None);

            destination = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_MotionBlurTarget", true, FilterMode.Bilinear);

            TextureHandle motionVectorColor = resourceData.motionVectorColor;
            TextureHandle cameraDepthTexture = resourceData.cameraDepthTexture;

            var mode = m_MotionBlur.mode.value;
            int passIndex = (int)m_MotionBlur.quality.value;
            passIndex += (mode == MotionBlurMode.CameraAndObjects) ? 3 : 0;

            using (var builder = renderGraph.AddRasterRenderPass<MotionBlurPassData>("Motion Blur", out var passData, ProfilingSampler.Get(URPProfileId.RG_MotionBlur)))
            {
                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destination;
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);

                if (mode == MotionBlurMode.CameraAndObjects)
                {
                    Debug.Assert(motionVectorColor.IsValid(), "Motion vectors are invalid. Per-object motion blur requires a motion vector texture.");

                    passData.motionVectors = motionVectorColor;
                    builder.UseTexture(motionVectorColor, AccessFlags.Read);
                }
                else
                {
                    passData.motionVectors = TextureHandle.nullHandle;
                }

                builder.UseTexture(cameraDepthTexture, AccessFlags.Read);
                passData.material = material;
                passData.passIndex = passIndex;
                passData.camera = cameraData.camera;
                passData.xr = cameraData.xr;
                passData.intensity = m_MotionBlur.intensity.value;
                passData.clamp = m_MotionBlur.clamp.value;
                builder.SetRenderFunc((MotionBlurPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    UpdateMotionBlurMatrices(ref data.material, data.camera, data.xr);

                    data.material.SetFloat("_Intensity", data.intensity);
                    data.material.SetFloat("_Clamp", data.clamp);

                    PostProcessUtils.SetSourceSize(cmd, data.sourceTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, data.material, data.passIndex);
                });

                return;
            }
        }
#endregion

#region LensFlareDataDriven
        private class LensFlarePassData
        {
            internal TextureHandle destinationTexture;
            internal RenderTextureDescriptor sourceDescriptor;
            internal UniversalCameraData cameraData;
            internal Material material;
            internal Rect viewport;
            internal float paniniDistance;
            internal float paniniCropToFit;
            internal float width;
            internal float height;
            internal bool usePanini;
        }

        void LensFlareDataDrivenComputeOcclusion(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData)
        {
            if (!LensFlareCommonSRP.IsOcclusionRTCompatible())
                return;

            using (var builder = renderGraph.AddUnsafePass<LensFlarePassData>("Lens Flare Compute Occlusion", out var passData, ProfilingSampler.Get(URPProfileId.LensFlareDataDrivenComputeOcclusion)))
            {
                RTHandle occH = LensFlareCommonSRP.occlusionRT;
                TextureHandle occlusionHandle = renderGraph.ImportTexture(LensFlareCommonSRP.occlusionRT);
                passData.destinationTexture = occlusionHandle;
                builder.UseTexture(occlusionHandle, AccessFlags.Write);
                passData.cameraData = cameraData;
                passData.viewport = cameraData.pixelRect;
                passData.material = m_Materials.lensFlareDataDriven;
                passData.width = (float)m_Descriptor.width;
                passData.height = (float)m_Descriptor.height;
                if (m_PaniniProjection.IsActive())
                {
                    passData.usePanini = true;
                    passData.paniniDistance = m_PaniniProjection.distance.value;
                    passData.paniniCropToFit = m_PaniniProjection.cropToFit.value;
                }
                else
                {
                    passData.usePanini = false;
                    passData.paniniDistance = 1.0f;
                    passData.paniniCropToFit = 1.0f;
                }

                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);

                builder.SetRenderFunc(
                    (LensFlarePassData data, UnsafeGraphContext ctx) =>
                    {
                        Camera camera = data.cameraData.camera;
                        XRPass xr = data.cameraData.xr;

                        Matrix4x4 nonJitteredViewProjMatrix0;
                        int xrId0;
#if ENABLE_VR && ENABLE_XR_MODULE
                        // Not VR or Multi-Pass
                        if (xr.enabled)
                        {
                            if (xr.singlePassEnabled)
                            {
                                nonJitteredViewProjMatrix0 = GL.GetGPUProjectionMatrix(data.cameraData.GetProjectionMatrixNoJitter(0), true) * data.cameraData.GetViewMatrix(0);
                                xrId0 = 0;
                            }
                            else
                            {
                                var gpuNonJitteredProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                                nonJitteredViewProjMatrix0 = gpuNonJitteredProj * camera.worldToCameraMatrix;
                                xrId0 = data.cameraData.xr.multipassId;
                            }
                        }
                        else
                        {
                            nonJitteredViewProjMatrix0 = GL.GetGPUProjectionMatrix(data.cameraData.GetProjectionMatrixNoJitter(0), true) * data.cameraData.GetViewMatrix(0);
                            xrId0 = 0;
                        }
#else
                        var gpuNonJitteredProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                        nonJitteredViewProjMatrix0 = gpuNonJitteredProj * camera.worldToCameraMatrix;
                        xrId0 = xr.multipassId;
#endif

                        LensFlareCommonSRP.ComputeOcclusion(
                            data.material, camera, xr, xr.multipassId,
                            data.width, data.height,
                            data.usePanini, data.paniniDistance, data.paniniCropToFit, true,
                            camera.transform.position,
                            nonJitteredViewProjMatrix0,
                            ctx.cmd,
                            false, false, null, null, null);


#if ENABLE_VR && ENABLE_XR_MODULE
                        if (xr.enabled && xr.singlePassEnabled)
                        {
                            //ctx.cmd.SetGlobalTexture(m_Depth.name, m_Depth.nameID);

                            for (int xrIdx = 1; xrIdx < xr.viewCount; ++xrIdx)
                            {
                                Matrix4x4 gpuVPXR = GL.GetGPUProjectionMatrix(data.cameraData.GetProjectionMatrixNoJitter(xrIdx), true) * data.cameraData.GetViewMatrix(xrIdx);

                                // Bypass single pass version
                                LensFlareCommonSRP.ComputeOcclusion(
                                    data.material, camera, xr, xrIdx,
                                    data.width, data.height,
                                    data.usePanini, data.paniniDistance, data.paniniCropToFit, true,
                                    camera.transform.position,
                                    gpuVPXR,
                                    ctx.cmd,
                                    false, false, null, null, null);
                            }
                        }
#endif
                    });
            }
        }

        public void RenderLensFlareDataDriven(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData, in TextureHandle destination)
        {
            using (var builder = renderGraph.AddUnsafePass<LensFlarePassData>("Lens Flare Data Driven Pass", out var passData, ProfilingSampler.Get(URPProfileId.LensFlareDataDriven)))
            {
                // Use WriteTexture here because DoLensFlareDataDrivenCommon will call SetRenderTarget internally.
                // TODO RENDERGRAPH: convert SRP core lens flare to be rendergraph friendly
                passData.destinationTexture = destination;
                builder.UseTexture(destination, AccessFlags.Write);
                passData.sourceDescriptor = m_Descriptor;
                passData.cameraData = cameraData;
                passData.material = m_Materials.lensFlareDataDriven;
                passData.width = (float)m_Descriptor.width;
                passData.height = (float)m_Descriptor.height;
                passData.viewport = cameraData.pixelRect;
                if (m_PaniniProjection.IsActive())
                {
                    passData.usePanini = true;
                    passData.paniniDistance = m_PaniniProjection.distance.value;
                    passData.paniniCropToFit = m_PaniniProjection.cropToFit.value;
                }
                else
                {
                    passData.usePanini = false;
                    passData.paniniDistance = 1.0f;
                    passData.paniniCropToFit = 1.0f;
                }
                if (LensFlareCommonSRP.IsOcclusionRTCompatible())
                {
                    TextureHandle occlusionHandle = renderGraph.ImportTexture(LensFlareCommonSRP.occlusionRT);
                    builder.UseTexture(occlusionHandle, AccessFlags.Read);
                }
                else
                {
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                }

                builder.SetRenderFunc((LensFlarePassData data, UnsafeGraphContext ctx) =>
                {
                    Camera camera = data.cameraData.camera;
                    XRPass xr = data.cameraData.xr;

#if ENABLE_VR && ENABLE_XR_MODULE
                    // Not VR or Multi-Pass
                    if (!xr.enabled ||
                        (xr.enabled && !xr.singlePassEnabled))
#endif
                    {
                        var gpuNonJitteredProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                        Matrix4x4 nonJitteredViewProjMatrix0 = gpuNonJitteredProj * camera.worldToCameraMatrix;

                        LensFlareCommonSRP.DoLensFlareDataDrivenCommon(
                            data.material, data.cameraData.camera, data.viewport, xr, data.cameraData.xr.multipassId,
                            data.width, data.height,
                            data.usePanini, data.paniniDistance, data.paniniCropToFit,
                            true,
                            camera.transform.position,
                            nonJitteredViewProjMatrix0,
                            ctx.cmd,
                            false, false, null, null,
                            data.destinationTexture,
                            (Light light, Camera cam, Vector3 wo) => { return GetLensFlareLightAttenuation(light, cam, wo); },
                            false);
                    }
#if ENABLE_VR && ENABLE_XR_MODULE
                    else
                    {
                        for (int xrIdx = 0; xrIdx < xr.viewCount; ++xrIdx)
                        {
                            Matrix4x4 nonJitteredViewProjMatrix_k = GL.GetGPUProjectionMatrix(data.cameraData.GetProjectionMatrixNoJitter(xrIdx), true) * data.cameraData.GetViewMatrix(xrIdx);

                            LensFlareCommonSRP.DoLensFlareDataDrivenCommon(
                                data.material, data.cameraData.camera, data.viewport, xr, data.cameraData.xr.multipassId,
                                data.width, data.height,
                                data.usePanini, data.paniniDistance, data.paniniCropToFit,
                                true,
                                camera.transform.position,
                                nonJitteredViewProjMatrix_k,
                                ctx.cmd,
                                false, false, null, null,
                                data.destinationTexture,
                                (Light light, Camera cam, Vector3 wo) => { return GetLensFlareLightAttenuation(light, cam, wo); },
                                false);
                        }
                    }
#endif
                });
            }
        }
#endregion

#region LensFlareScreenSpace

        private class LensFlareScreenSpacePassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle streakTmpTexture;
            internal TextureHandle streakTmpTexture2;
            internal TextureHandle originalBloomTexture;
            internal TextureHandle screenSpaceLensFlareBloomMipTexture;
            internal TextureHandle result;
            internal RenderTextureDescriptor sourceDescriptor;
            internal Camera camera;
            internal Material material;
            internal int downsample;
        }

        public TextureHandle RenderLensFlareScreenSpace(RenderGraph renderGraph, Camera camera, in TextureHandle destination, TextureHandle originalBloomTexture, TextureHandle screenSpaceLensFlareBloomMipTexture, bool enableXR)
        {
            var downsample = (int) m_LensFlareScreenSpace.resolution.value;

            int width = m_Descriptor.width / downsample;
            int height = m_Descriptor.height / downsample;

            var streakTextureDesc = GetCompatibleDescriptor(m_Descriptor, width, height, m_DefaultHDRFormat);
            var streakTmpTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, streakTextureDesc, "_StreakTmpTexture", true, FilterMode.Bilinear);
            var streakTmpTexture2 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, streakTextureDesc, "_StreakTmpTexture2", true, FilterMode.Bilinear);
            var resultTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, streakTextureDesc, "Lens Flare Screen Space Result", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddUnsafePass<LensFlareScreenSpacePassData>("Lens Flare Screen Space Pass", out var passData, ProfilingSampler.Get(URPProfileId.LensFlareScreenSpace)))
            {
                // Use WriteTexture here because DoLensFlareScreenSpaceCommon will call SetRenderTarget internally.
                // TODO RENDERGRAPH: convert SRP core lensflare to be rendergraph friendly
                passData.destinationTexture = destination;
                builder.UseTexture(destination, AccessFlags.Write);
                passData.streakTmpTexture = streakTmpTexture;
                builder.UseTexture(streakTmpTexture, AccessFlags.ReadWrite);
                passData.streakTmpTexture2 = streakTmpTexture2;
                builder.UseTexture(streakTmpTexture2, AccessFlags.ReadWrite);
                passData.screenSpaceLensFlareBloomMipTexture = screenSpaceLensFlareBloomMipTexture;
                builder.UseTexture(screenSpaceLensFlareBloomMipTexture, AccessFlags.ReadWrite);
                passData.originalBloomTexture = originalBloomTexture;
                builder.UseTexture(originalBloomTexture, AccessFlags.ReadWrite);
                passData.sourceDescriptor = m_Descriptor;
                passData.camera = camera;
                passData.material = m_Materials.lensFlareScreenSpace;
                passData.downsample = downsample;
                passData.result = resultTexture;
                builder.UseTexture(resultTexture, AccessFlags.Write);

                builder.SetRenderFunc((LensFlareScreenSpacePassData data, UnsafeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var camera = data.camera;

                    LensFlareCommonSRP.DoLensFlareScreenSpaceCommon(
                        m_Materials.lensFlareScreenSpace,
                        camera,
                        (float)data.sourceDescriptor.width,
                        (float)data.sourceDescriptor.height,
                        m_LensFlareScreenSpace.tintColor.value,
                        data.originalBloomTexture,
                        data.screenSpaceLensFlareBloomMipTexture,
                        null, // We don't have any spectral LUT in URP
                        data.streakTmpTexture,
                        data.streakTmpTexture2,
                        new Vector4(
                            m_LensFlareScreenSpace.intensity.value,
                            m_LensFlareScreenSpace.firstFlareIntensity.value,
                            m_LensFlareScreenSpace.secondaryFlareIntensity.value,
                            m_LensFlareScreenSpace.warpedFlareIntensity.value),
                        new Vector4(
                            m_LensFlareScreenSpace.vignetteEffect.value,
                            m_LensFlareScreenSpace.startingPosition.value,
                            m_LensFlareScreenSpace.scale.value,
                            0), // Free slot, not used
                        new Vector4(
                            m_LensFlareScreenSpace.samples.value,
                            m_LensFlareScreenSpace.sampleDimmer.value,
                            m_LensFlareScreenSpace.chromaticAbberationIntensity.value,
                            0), // No need to pass a chromatic aberration sample count, hardcoded at 3 in shader
                        new Vector4(
                            m_LensFlareScreenSpace.streaksIntensity.value,
                            m_LensFlareScreenSpace.streaksLength.value,
                            m_LensFlareScreenSpace.streaksOrientation.value,
                            m_LensFlareScreenSpace.streaksThreshold.value),
                        new Vector4(
                            data.downsample,
                            m_LensFlareScreenSpace.warpedFlareScale.value.x,
                            m_LensFlareScreenSpace.warpedFlareScale.value.y,
                            0), // Free slot, not used
                        cmd,
                        data.result,
                        false);
                });
                return passData.originalBloomTexture;
            }
        }

#endregion

        static private void ScaleViewportAndBlit(RasterCommandBuffer cmd, RTHandle sourceTextureHdl, RTHandle dest, UniversalCameraData cameraData, Material material)
        {
            Vector4 scaleBias = RenderingUtils.GetFinalBlitScaleBias(sourceTextureHdl, dest, cameraData);
            RenderTargetIdentifier cameraTarget = BuiltinRenderTextureType.CameraTarget;
        #if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
                cameraTarget = cameraData.xr.renderTarget;
        #endif
            if (dest.nameID == cameraTarget || cameraData.targetTexture != null)
                cmd.SetViewport(cameraData.pixelRect);

            Blitter.BlitTexture(cmd, sourceTextureHdl, scaleBias, material, 0);
        }

#region FinalPass
        private class PostProcessingFinalSetupPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal UniversalCameraData cameraData;
        }

        public void RenderFinalSetup(RenderGraph renderGraph, UniversalCameraData cameraData, in TextureHandle source, in TextureHandle destination, bool isFxaaEnabled, bool isFsrEnabled, HDROutputUtils.Operation hdrOperations)
        {
            // Scaled FXAA
            using (var builder = renderGraph.AddRasterRenderPass<PostProcessingFinalSetupPassData>("Postprocessing Final Setup Pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_FinalSetup)))
            {
                Material material = m_Materials.scalingSetup;

                if (isFxaaEnabled)
                    material.EnableKeyword(ShaderKeywordStrings.Fxaa);

                if (isFsrEnabled)
                    material.EnableKeyword(hdrOperations.HasFlag(HDROutputUtils.Operation.ColorEncoding) ? ShaderKeywordStrings.Gamma20AndHDRInput : ShaderKeywordStrings.Gamma20);

                if (hdrOperations.HasFlag(HDROutputUtils.Operation.ColorEncoding))
                    SetupHDROutput(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, material, hdrOperations);

                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destination;
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.cameraData = cameraData;
                passData.material = material;

                builder.SetRenderFunc((PostProcessingFinalSetupPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    PostProcessUtils.SetSourceSize(cmd, sourceTextureHdl);

                    ScaleViewportAndBlit(context.cmd, sourceTextureHdl, data.destinationTexture, data.cameraData, data.material);
                });
                return;
            }
        }

        private class PostProcessingFinalFSRScalePassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
        }

        public void RenderFinalFSRScale(RenderGraph renderGraph, in TextureHandle source, in TextureHandle destination)
        {
            // FSR upscale
            m_Materials.easu.shaderKeywords = null;

            using (var builder = renderGraph.AddRasterRenderPass<PostProcessingFinalFSRScalePassData>("Postprocessing Final FSR Scale Pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_FinalFSRScale)))
            {
                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destination;
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.material = m_Materials.easu;

                builder.SetRenderFunc((PostProcessingFinalFSRScalePassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var sourceTex = data.sourceTexture;
                    var destTex = data.destinationTexture;
                    var material = data.material;
                    RTHandle sourceHdl = (RTHandle)sourceTex;
                    RTHandle destHdl = (RTHandle)destTex;

                    var fsrInputSize = new Vector2(sourceHdl.referenceSize.x, sourceHdl.referenceSize.y);
                    var fsrOutputSize = new Vector2(destHdl.referenceSize.x, destHdl.referenceSize.y);
                    FSRUtils.SetEasuConstants(cmd, fsrInputSize, fsrInputSize, fsrOutputSize);

                    Vector2 viewportScale = sourceHdl.useScaling ? new Vector2(sourceHdl.rtHandleProperties.rtHandleScale.x, sourceHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceHdl, viewportScale, material, 0);
                });
                return;
            }
        }

        private class PostProcessingFinalBlitPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal UniversalCameraData cameraData;
            internal FinalBlitSettings settings;
        }

        /// <summary>
        /// Final blit settings.
        /// </summary>
        public struct FinalBlitSettings
        {
            /// <summary>Is FXAA enabled</summary>
            public bool isFxaaEnabled;
            /// <summary>Is FSR Enabled.</summary>
            public bool isFsrEnabled;
            /// <summary>Is TAA sharpening enabled.</summary>
            public bool isTaaSharpeningEnabled;
            /// <summary>True if final blit requires HDR output.</summary>
            public bool requireHDROutput;
            /// <summary>True if final blit needs to resolve to debug screen.</summary>
            public bool resolveToDebugScreen;

            /// <summary>
            /// Create FinalBlitSettings
            /// </summary>
            /// <returns>New FinalBlitSettings</returns>
            public static FinalBlitSettings Create()
            {
                FinalBlitSettings s = new FinalBlitSettings();
                s.isFxaaEnabled = false;
                s.isFsrEnabled = false;
                s.isTaaSharpeningEnabled = false;
                s.requireHDROutput = false;
                s.resolveToDebugScreen = false;
                return s;
            }
        };

        public void RenderFinalBlit(RenderGraph renderGraph, UniversalCameraData cameraData, in TextureHandle source, in TextureHandle overlayUITexture, in TextureHandle postProcessingTarget, ref FinalBlitSettings settings)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PostProcessingFinalBlitPassData>("Postprocessing Final Blit Pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_FinalBlit)))
            {
                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = postProcessingTarget;
                builder.SetRenderAttachment(postProcessingTarget, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.cameraData = cameraData;
                passData.material = m_Materials.finalPass;
                passData.settings = settings;

                if (settings.requireHDROutput && m_EnableColorEncodingIfNeeded)
                    builder.UseTexture(overlayUITexture, AccessFlags.Read);

                builder.SetRenderFunc(static (PostProcessingFinalBlitPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var material = data.material;
                    var isFxaaEnabled = data.settings.isFxaaEnabled;
                    var isFsrEnabled = data.settings.isFsrEnabled;
                    var isRcasEnabled = data.settings.isTaaSharpeningEnabled;
                    var requireHDROutput = data.settings.requireHDROutput;
                    var resolveToDebugScreen = data.settings.resolveToDebugScreen;
                    RTHandle sourceTextureHdl = data.sourceTexture;
                    RTHandle destinationTextureHdl = data.destinationTexture;

                    PostProcessUtils.SetSourceSize(cmd, data.sourceTexture);

                    if (isFxaaEnabled)
                        material.EnableKeyword(ShaderKeywordStrings.Fxaa);

                    if (isFsrEnabled)
                    {
                        // RCAS
                        // Use the override value if it's available, otherwise use the default.
                        float sharpness = data.cameraData.fsrOverrideSharpness ? data.cameraData.fsrSharpness : FSRUtils.kDefaultSharpnessLinear;

                        // Set up the parameters for the RCAS pass unless the sharpness value indicates that it wont have any effect.
                        if (data.cameraData.fsrSharpness > 0.0f)
                        {
                            // RCAS is performed during the final post blit, but we set up the parameters here for better logical grouping.
                            material.EnableKeyword(requireHDROutput ? ShaderKeywordStrings.EasuRcasAndHDRInput : ShaderKeywordStrings.Rcas);
                            FSRUtils.SetRcasConstantsLinear(cmd, sharpness);
                        }
                    }
                    else if (isRcasEnabled)   // RCAS only
                    {
                        // Reuse RCAS as a standalone sharpening filter for TAA.
                        // If FSR is enabled then it overrides the sharpening/TAA setting and we skip it.
                        material.EnableKeyword(ShaderKeywordStrings.Rcas);
                        FSRUtils.SetRcasConstantsLinear(cmd, data.cameraData.taaSettings.contrastAdaptiveSharpening);
                    }

                    bool isRenderToBackBufferTarget = !data.cameraData.isSceneViewCamera;
#if ENABLE_VR && ENABLE_XR_MODULE
                    if (data.cameraData.xr.enabled)
                        isRenderToBackBufferTarget = destinationTextureHdl == data.cameraData.xr.renderTarget;
#endif
                    // HDR debug views force-renders to DebugScreenTexture.
                    isRenderToBackBufferTarget &= !resolveToDebugScreen;

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;

                    // We y-flip if
                    // 1) we are blitting from render texture to back buffer(UV starts at bottom) and
                    // 2) renderTexture starts UV at top
                    bool yflip = isRenderToBackBufferTarget && data.cameraData.targetTexture == null && SystemInfo.graphicsUVStartsAtTop;
                    Vector4 scaleBias = yflip ? new Vector4(viewportScale.x, -viewportScale.y, 0, viewportScale.y) : new Vector4(viewportScale.x, viewportScale.y, 0, 0);

                    cmd.SetViewport(data.cameraData.pixelRect);
                    Blitter.BlitTexture(cmd, sourceTextureHdl, scaleBias, material, 0);
                });

                return;
            }
        }

        public void RenderFinalPassRenderGraph(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle source, in TextureHandle overlayUITexture, in TextureHandle postProcessingTarget, bool enableColorEncodingIfNeeded)
        {
            var stack = VolumeManager.instance.stack;
            m_Tonemapping = stack.GetComponent<Tonemapping>();
            m_FilmGrain = stack.GetComponent<FilmGrain>();
            m_Tonemapping = stack.GetComponent<Tonemapping>();

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            var material = m_Materials.finalPass;

            material.shaderKeywords = null;

            FinalBlitSettings settings = FinalBlitSettings.Create();

            // TODO RENDERGRAPH: when we remove the old path we should review the naming of these variables...
            // m_HasFinalPass is used to let FX passes know when they are not being called by the actual final pass, so they can skip any "final work"
            m_HasFinalPass = false;
            // m_IsFinalPass is used by effects called by RenderFinalPassRenderGraph, so we let them know that we are in a final PP pass
            m_IsFinalPass = true;
            m_EnableColorEncodingIfNeeded = enableColorEncodingIfNeeded;

            if (m_FilmGrain.IsActive())
            {
                material.EnableKeyword(ShaderKeywordStrings.FilmGrain);
                PostProcessUtils.ConfigureFilmGrain(
                    m_Data,
                    m_FilmGrain,
                    cameraData.pixelWidth, cameraData.pixelHeight,
                    material
                );
            }

            if (cameraData.isDitheringEnabled)
            {
                material.EnableKeyword(ShaderKeywordStrings.Dithering);
                m_DitheringTextureIndex = PostProcessUtils.ConfigureDithering(
                    m_Data,
                    m_DitheringTextureIndex,
                    cameraData.pixelWidth, cameraData.pixelHeight,
                    material
                );
            }

            if (RequireSRGBConversionBlitToBackBuffer(cameraData.requireSrgbConversion))
                material.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

            HDROutputUtils.Operation hdrOperations = HDROutputUtils.Operation.None;
            settings.requireHDROutput = RequireHDROutput(cameraData);
            if (settings.requireHDROutput)
            {
                // If there is a final post process pass, it's always the final pass so do color encoding
                hdrOperations = m_EnableColorEncodingIfNeeded ? HDROutputUtils.Operation.ColorEncoding : HDROutputUtils.Operation.None;
                // If the color space conversion wasn't applied by the uber pass, do it here
                if (!cameraData.postProcessEnabled)
                    hdrOperations |= HDROutputUtils.Operation.ColorConversion;

                SetupHDROutput(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, material, hdrOperations);
            }
            DebugHandler debugHandler = GetActiveDebugHandler(cameraData);
            bool resolveToDebugScreen = debugHandler != null && debugHandler.WriteToDebugScreenTexture(cameraData.resolveFinalTarget);
            debugHandler?.UpdateShaderGlobalPropertiesForFinalValidationPass(renderGraph, cameraData, !m_HasFinalPass && !resolveToDebugScreen);

            bool outputToHDR = cameraData.isHDROutputActive;
            settings.isFxaaEnabled = (cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing);
            settings.isFsrEnabled = ((cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (cameraData.upscalingFilter == ImageUpscalingFilter.FSR));

            // Reuse RCAS pass as an optional standalone post sharpening pass for TAA.
            // This avoids the cost of EASU and is available for other upscaling options.
            // If FSR is enabled then FSR settings override the TAA settings and we perform RCAS only once.
            // If STP is enabled, then TAA sharpening has already been performed inside STP.
            settings.isTaaSharpeningEnabled = (cameraData.IsTemporalAAEnabled() && cameraData.taaSettings.contrastAdaptiveSharpening > 0.0f) && !settings.isFsrEnabled && !cameraData.IsSTPEnabled();

            var tempRtDesc = cameraData.cameraTargetDescriptor;
            tempRtDesc.msaaSamples = 1;
            tempRtDesc.depthBufferBits = 0;

            // Select a UNORM format since we've already performed tonemapping. (Values are in 0-1 range)
            // This improves precision and is required if we want to avoid excessive banding when FSR is in use.
            if (!settings.requireHDROutput)
                tempRtDesc.graphicsFormat = UniversalRenderPipeline.MakeUnormRenderTextureGraphicsFormat();

            var scalingSetupTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tempRtDesc, "scalingSetupTarget", true, FilterMode.Point);
            var upscaleRtDesc = tempRtDesc;
            upscaleRtDesc.width = cameraData.pixelWidth;
            upscaleRtDesc.height = cameraData.pixelHeight;
            var upScaleTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, upscaleRtDesc, "_UpscaledTexture", true, FilterMode.Point);

            var currentSource = source;
            if (cameraData.imageScalingMode != ImageScalingMode.None)
            {
                // When FXAA is enabled in scaled renders, we execute it in a separate blit since it's not designed to be used in
                // situations where the input and output resolutions do not match.
                // When FSR is active, we always need an additional pass since it has a very particular color encoding requirement.

                // NOTE: An ideal implementation could inline this color conversion logic into the UberPost pass, but the current code structure would make
                //       this process very complex. Specifically, we'd need to guarantee that the uber post output is always written to a UNORM format render
                //       target in order to preserve the precision of specially encoded color data.
                bool isSetupRequired = (settings.isFxaaEnabled || settings.isFsrEnabled);

                // When FXAA is needed while scaling is active, we must perform it before the scaling takes place.
                if (isSetupRequired)
                {
                    RenderFinalSetup(renderGraph, cameraData, in currentSource, in scalingSetupTarget, settings.isFxaaEnabled, settings.isFsrEnabled, hdrOperations);
                    currentSource = scalingSetupTarget;

                    // Indicate that we no longer need to perform FXAA in the final pass since it was already perfomed here.
                    settings.isFxaaEnabled = false;
                }

                switch (cameraData.imageScalingMode)
                {
                    case ImageScalingMode.Upscaling:
                    {
                        switch (cameraData.upscalingFilter)
                        {
                            case ImageUpscalingFilter.Point:
                            {
                                // TAA post sharpening is an RCAS pass, avoid overriding it with point sampling.
                                if (!settings.isTaaSharpeningEnabled)
                                    material.EnableKeyword(ShaderKeywordStrings.PointSampling);
                                break;
                            }
                            case ImageUpscalingFilter.Linear:
                            {
                                break;
                            }
                            case ImageUpscalingFilter.FSR:
                            {
                                RenderFinalFSRScale(renderGraph, in currentSource, in upScaleTarget);
                                currentSource = upScaleTarget;
                                break;
                            }
                        }
                        break;
                    }
                    case ImageScalingMode.Downscaling:
                    {
                        // In the downscaling case, we don't perform any sort of filter override logic since we always want linear filtering
                        // and it's already the default option in the shader.

                        // Also disable TAA post sharpening pass when downscaling.
                        settings.isTaaSharpeningEnabled = false;
                        break;
                    }
                }
            }
            else if (settings.isFxaaEnabled)
            {
                // In unscaled renders, FXAA can be safely performed in the FinalPost shader
                material.EnableKeyword(ShaderKeywordStrings.Fxaa);
            }

            RenderFinalBlit(renderGraph, cameraData, in currentSource, in overlayUITexture, in postProcessingTarget, ref settings);
        }
#endregion

#region UberPost
        private class UberPostPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal TextureHandle lutTexture;
            internal Vector4 lutParams;
            internal TextureHandle userLutTexture;
            internal Vector4 userLutParams;
            internal Material material;
            internal UniversalCameraData cameraData;
            internal TonemappingMode toneMappingMode;
            internal bool isHdr;
            internal bool isBackbuffer;
        }

        public void RenderUberPost(RenderGraph renderGraph, UniversalCameraData cameraData, UniversalPostProcessingData postProcessingData, in TextureHandle sourceTexture, in TextureHandle destTexture, in TextureHandle lutTexture, in TextureHandle overlayUITexture, bool requireHDROutput, bool resolveToDebugScreen)
        {
            var material = m_Materials.uber;
            bool hdr = postProcessingData.gradingMode == ColorGradingMode.HighDynamicRange;
            int lutHeight = postProcessingData.lutSize;
            int lutWidth = lutHeight * lutHeight;

            // Source material setup
            float postExposureLinear = Mathf.Pow(2f, m_ColorAdjustments.postExposure.value);
            Vector4 lutParams = new Vector4(1f / lutWidth, 1f / lutHeight, lutHeight - 1f, postExposureLinear);

            RTHandle userLutRThdl = m_ColorLookup.texture.value ? RTHandles.Alloc(m_ColorLookup.texture.value) : null;
            TextureHandle userLutTexture = userLutRThdl != null ? renderGraph.ImportTexture(userLutRThdl) : TextureHandle.nullHandle;
            Vector4 userLutParams = !m_ColorLookup.IsActive()
                ? Vector4.zero
                : new Vector4(1f / m_ColorLookup.texture.value.width,
                    1f / m_ColorLookup.texture.value.height,
                    m_ColorLookup.texture.value.height - 1f,
                    m_ColorLookup.contribution.value);

            using (var builder = renderGraph.AddRasterRenderPass<UberPostPassData>("Postprocessing Uber Post Pass", out var passData, ProfilingSampler.Get(URPProfileId.RG_UberPost)))
            {
                UniversalRenderer renderer = cameraData.renderer as UniversalRenderer;
                if (cameraData.requiresDepthTexture && renderer != null)
                {
                    if (renderer.renderingModeActual != RenderingMode.Deferred)
                        builder.UseGlobalTexture(s_CameraDepthTextureID);
                    else if (renderer.deferredLights.GbufferDepthIndex != -1)
                        builder.UseGlobalTexture(DeferredLights.k_GBufferShaderPropertyIDs[renderer.deferredLights.GbufferDepthIndex]);
                }

                if (cameraData.requiresOpaqueTexture && renderer != null)
                    builder.UseGlobalTexture(s_CameraOpaqueTextureID);

                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destTexture;
                builder.SetRenderAttachment(destTexture, 0, AccessFlags.Write);
                passData.sourceTexture = sourceTexture;
                builder.UseTexture(sourceTexture, AccessFlags.Read);
                passData.lutTexture = lutTexture;
                builder.UseTexture(lutTexture, AccessFlags.Read);
                passData.lutParams = lutParams;
                if (userLutTexture.IsValid())
                {
                    passData.userLutTexture = userLutTexture;
                    builder.UseTexture(userLutTexture, AccessFlags.Read);
                }

                if (m_Bloom.IsActive())
                    builder.UseTexture(_BloomMipUp[0], AccessFlags.Read);
                if (requireHDROutput && m_EnableColorEncodingIfNeeded)
                    builder.UseTexture(overlayUITexture, AccessFlags.Read);
                passData.userLutParams = userLutParams;
                passData.cameraData = cameraData;
                passData.material = material;
                passData.toneMappingMode = m_Tonemapping.mode.value;
                passData.isHdr = hdr;

                builder.SetRenderFunc((UberPostPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var camera = data.cameraData.camera;
                    var material = data.material;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    material.SetTexture(ShaderConstants._InternalLut, data.lutTexture);
                    material.SetVector(ShaderConstants._Lut_Params, data.lutParams);
                    material.SetTexture(ShaderConstants._UserLut, data.userLutTexture);
                    material.SetVector(ShaderConstants._UserLut_Params, data.userLutParams);

                    if (data.isHdr)
                    {
                        material.EnableKeyword(ShaderKeywordStrings.HDRGrading);
                    }
                    else
                    {
                        switch (data.toneMappingMode)
                        {
                            case TonemappingMode.Neutral: material.EnableKeyword(ShaderKeywordStrings.TonemapNeutral); break;
                            case TonemappingMode.ACES: material.EnableKeyword(ShaderKeywordStrings.TonemapACES); break;
                            default: break; // None
                        }
                    }

                    // Done with Uber, blit it
                    ScaleViewportAndBlit(cmd, sourceTextureHdl, data.destinationTexture, data.cameraData, material);
                });

                return;
            }
        }
#endregion

        private class PostFXSetupPassData { }

        public void RenderPostProcessingRenderGraph(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColorTexture, in TextureHandle lutTexture, in TextureHandle overlayUITexture, in TextureHandle postProcessingTarget, bool hasFinalPass, bool resolveToDebugScreen, bool enableColorEndingIfNeeded)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalPostProcessingData postProcessingData = frameData.Get<UniversalPostProcessingData>();

            var stack = VolumeManager.instance.stack;
            m_DepthOfField = stack.GetComponent<DepthOfField>();
            m_MotionBlur = stack.GetComponent<MotionBlur>();
            m_PaniniProjection = stack.GetComponent<PaniniProjection>();
            m_Bloom = stack.GetComponent<Bloom>();
            m_LensFlareScreenSpace = stack.GetComponent<ScreenSpaceLensFlare>();
            m_LensDistortion = stack.GetComponent<LensDistortion>();
            m_ChromaticAberration = stack.GetComponent<ChromaticAberration>();
            m_Vignette = stack.GetComponent<Vignette>();
            m_ColorLookup = stack.GetComponent<ColorLookup>();
            m_ColorAdjustments = stack.GetComponent<ColorAdjustments>();
            m_Tonemapping = stack.GetComponent<Tonemapping>();
            m_FilmGrain = stack.GetComponent<FilmGrain>();
            m_UseFastSRGBLinearConversion = postProcessingData.useFastSRGBLinearConversion;
            m_SupportDataDrivenLensFlare = postProcessingData.supportDataDrivenLensFlare;
            m_SupportScreenSpaceLensFlare = postProcessingData.supportScreenSpaceLensFlare;
            m_Descriptor = cameraData.cameraTargetDescriptor;
            m_Descriptor.useMipMap = false;
            m_Descriptor.autoGenerateMips = false;
            m_HasFinalPass = hasFinalPass;
            m_EnableColorEncodingIfNeeded = enableColorEndingIfNeeded;


            ref ScriptableRenderer renderer = ref cameraData.renderer;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            //We blit back and forth without msaa untill the last blit.
            bool useStopNan = cameraData.isStopNaNEnabled && m_Materials.stopNaN != null;
            bool useSubPixelMorpAA = cameraData.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            var dofMaterial = m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian ? m_Materials.gaussianDepthOfField : m_Materials.bokehDepthOfField;
            bool useDepthOfField = m_DepthOfField.IsActive() && !isSceneViewCamera && dofMaterial != null;
            bool useLensFlare = !LensFlareCommonSRP.Instance.IsEmpty() && m_SupportDataDrivenLensFlare;
            bool useLensFlareScreenSpace = m_LensFlareScreenSpace.IsActive() && m_SupportScreenSpaceLensFlare;
            bool useMotionBlur = m_MotionBlur.IsActive() && !isSceneViewCamera;
            bool usePaniniProjection = m_PaniniProjection.IsActive() && !isSceneViewCamera;
            bool isFsrEnabled = ((cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (cameraData.upscalingFilter == ImageUpscalingFilter.FSR));

            // Disable MotionBlur in EditMode, so that editing remains clear and readable.
            // NOTE: HDRP does the same via CoreUtils::AreAnimatedMaterialsEnabled().
            useMotionBlur = useMotionBlur && Application.isPlaying;

            // Note that enabling jitters uses the same CameraData::IsTemporalAAEnabled(). So if we add any other kind of overrides (like
            // disable useTemporalAA if another feature is disabled) then we need to put it in CameraData::IsTemporalAAEnabled() as opposed
            // to tweaking the value here.
            bool useTemporalAA = cameraData.IsTemporalAAEnabled();
            if (cameraData.antialiasing == AntialiasingMode.TemporalAntiAliasing && !useTemporalAA)
                TemporalAA.ValidateAndWarn(cameraData);

            // STP is only supported when TAA is enabled and all of its runtime requirements are met.
            // See the comments for IsSTPEnabled() for more information.
            bool useSTP = useTemporalAA && cameraData.IsSTPEnabled();

            using (var builder = renderGraph.AddRasterRenderPass<PostFXSetupPassData>("Setup PostFX passes", out var passData,
                ProfilingSampler.Get(URPProfileId.RG_SetupPostFX)))
            {
                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PostFXSetupPassData data, RasterGraphContext context) =>
                {
                    // Setup projection matrix for cmd.DrawMesh()
                    context.cmd.SetGlobalMatrix(ShaderConstants._FullscreenProjMat, GL.GetGPUProjectionMatrix(Matrix4x4.identity, true));
                });
            }

            TextureHandle currentSource = activeCameraColorTexture;

            // Optional NaN killer before post-processing kicks in
            // stopNaN may be null on Adreno 3xx. It doesn't support full shader level 3.5, but SystemInfo.graphicsShaderLevel is 35.
            if (useStopNan)
            {
                RenderStopNaN(renderGraph, cameraData.cameraTargetDescriptor, in currentSource, out var stopNaNTarget);
                currentSource = stopNaNTarget;
            }

            if(useSubPixelMorpAA)
            {
                RenderSMAA(renderGraph, resourceData, cameraData.antialiasingQuality, in currentSource, out var SMAATarget);
                currentSource = SMAATarget;
            }

            // Depth of Field
            // Adreno 3xx SystemInfo.graphicsShaderLevel is 35, but instancing support is disabled due to buggy drivers.
            // DOF shader uses #pragma target 3.5 which adds requirement for instancing support, thus marking the shader unsupported on those devices.
            if (useDepthOfField)
            {
                RenderDoF(renderGraph, resourceData, in currentSource, out var DoFTarget);
                currentSource = DoFTarget;
            }

            // Temporal Anti Aliasing
            if (useTemporalAA)
            {
                if (useSTP)
                {
                    RenderSTP(renderGraph, resourceData, cameraData, ref currentSource, out var StpTarget);
                    currentSource = StpTarget;
                }
                else
                {
                    RenderTemporalAA(renderGraph, resourceData, cameraData, ref currentSource, out var TemporalAATarget);
                    currentSource = TemporalAATarget;
                }
            }

            if(useMotionBlur)
            {
                RenderMotionBlur(renderGraph, resourceData, cameraData, in currentSource, out var MotionBlurTarget);
                currentSource = MotionBlurTarget;
            }

            if(usePaniniProjection)
            {
                RenderPaniniProjection(renderGraph, cameraData.camera, in currentSource, out var PaniniTarget);
                currentSource = PaniniTarget;
            }

            // Uberpost
            {
                // Reset uber keywords
                m_Materials.uber.shaderKeywords = null;

                // Bloom goes first
                bool bloomActive = m_Bloom.IsActive();
                //Even if bloom is not active we need the texture if the lensFlareScreenSpace pass is active.
                if (bloomActive || useLensFlareScreenSpace)
                {
                    RenderBloomTexture(renderGraph, currentSource, out var BloomTexture);

                    if (useLensFlareScreenSpace)
                    {
                        int maxBloomMip = Mathf.Clamp(m_LensFlareScreenSpace.bloomMip.value, 0, m_Bloom.maxIterations.value/2);
                        BloomTexture = RenderLensFlareScreenSpace(renderGraph, cameraData.camera, in currentSource, _BloomMipUp[0], _BloomMipUp[maxBloomMip], cameraData.xr.enabled);
                    }

                    UberPostSetupBloomPass(renderGraph, in BloomTexture, m_Materials.uber);
                }

                if (useLensFlare)
                {
                    LensFlareDataDrivenComputeOcclusion(renderGraph, resourceData, cameraData);
                    RenderLensFlareDataDriven(renderGraph, resourceData, cameraData, in currentSource);
                }

                // TODO RENDERGRAPH: Once we started removing the non-RG code pass in URP, we should move functions below to renderfunc so that material setup happens at
                // the same timeline of executing the rendergraph. Keep them here for now so we cound reuse non-RG code to reduce maintainance cost.
                SetupLensDistortion(m_Materials.uber, isSceneViewCamera);
                SetupChromaticAberration(m_Materials.uber);
                SetupVignette(m_Materials.uber, cameraData.xr);
                SetupGrain(cameraData, m_Materials.uber);
                SetupDithering(cameraData, m_Materials.uber);

                if (RequireSRGBConversionBlitToBackBuffer(cameraData.requireSrgbConversion))
                    m_Materials.uber.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

                if (m_UseFastSRGBLinearConversion)
                {
                    m_Materials.uber.EnableKeyword(ShaderKeywordStrings.UseFastSRGBLinearConversion);
                }

                bool requireHDROutput = RequireHDROutput(cameraData);
                if (requireHDROutput)
                {
                    // Color space conversion is already applied through color grading, do encoding if uber post is the last pass
                    // Otherwise encoding will happen in the final post process pass or the final blit pass
                    HDROutputUtils.Operation hdrOperations = !m_HasFinalPass && m_EnableColorEncodingIfNeeded ? HDROutputUtils.Operation.ColorEncoding : HDROutputUtils.Operation.None;

                    SetupHDROutput(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, m_Materials.uber, hdrOperations);
                }

                DebugHandler debugHandler = GetActiveDebugHandler(cameraData);
                debugHandler?.UpdateShaderGlobalPropertiesForFinalValidationPass(renderGraph, cameraData, !m_HasFinalPass && !resolveToDebugScreen);

                RenderUberPost(renderGraph, cameraData, postProcessingData, in currentSource, in postProcessingTarget, in lutTexture, in overlayUITexture, requireHDROutput, resolveToDebugScreen);
            }
        }
    }
}

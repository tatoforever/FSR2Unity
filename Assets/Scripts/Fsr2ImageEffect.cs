// Copyright (c) 2023 Nico de Poel
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FidelityFX
{
    /// <summary>
    /// This class is responsible for hooking into various Unity events and translating them to the FSR2 subsystem.
    /// This includes creation and destruction of the FSR2 context, as well as dispatching commands at the right time.
    /// This component also exposes various FSR2 parameters to the Unity inspector.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class Fsr2ImageEffect : MonoBehaviour
    {
        public IFsr2Callbacks Callbacks { get; set; } = new Fsr2CallbacksBase();
        
        [Tooltip("Standard scaling ratio presets.")]
        public Fsr2.QualityMode qualityMode = Fsr2.QualityMode.Quality;

        [Tooltip("Apply RCAS sharpening to the image after upscaling.")]
        public bool performSharpenPass = true;
        [Tooltip("Strength of the sharpening effect.")]
        [Range(0, 1)] public float sharpness = 0.8f;
        
        [Tooltip("Allow the use of half precision compute operations, potentially improving performance if the platform supports it.")]
        public bool enableFP16 = false;

        [Header("Exposure")]
        [Tooltip("Allow an exposure value to be computed internally. When set to false, either the provided exposure texture or a default exposure value will be used.")]
        public bool enableAutoExposure = true;
        [Tooltip("Value by which the input signal will be divided, to get back to the original signal produced by the game.")]
        public float preExposure = 1.0f;
        [Tooltip("Optional 1x1 texture containing the exposure value for the current frame.")]
        public Texture exposure = null;

        [Header("Reactivity, Transparency & Composition")] 
        [Tooltip("Optional texture to control the influence of the current frame on the reconstructed output. If unset, either an auto-generated or a default cleared reactive mask will be used.")]
        public Texture reactiveMask = null;
        [Tooltip("Optional texture for marking areas of specialist rendering which should be accounted for during the upscaling process. If unset, a default cleared mask will be used.")]
        public Texture transparencyAndCompositionMask = null;
        [Tooltip("Automatically generate a reactive mask based on the difference between opaque-only render output and the final render output including alpha transparencies.")]
        public bool autoGenerateReactiveMask = true;
        [Tooltip("Parameters to control the process of auto-generating a reactive mask.")]
        [SerializeField] private GenerateReactiveParameters generateReactiveParameters = new GenerateReactiveParameters();
        public GenerateReactiveParameters GenerateReactiveParams => generateReactiveParameters;
        
        [Serializable]
        public class GenerateReactiveParameters
        {
            [Range(0, 2)] public float scale = 0.5f;
            [Range(0, 1)] public float cutoffThreshold = 0.2f;
            [Range(0, 1)] public float binaryValue = 0.9f;
            public Fsr2.GenerateReactiveFlags flags = Fsr2.GenerateReactiveFlags.ApplyTonemap | Fsr2.GenerateReactiveFlags.ApplyThreshold | Fsr2.GenerateReactiveFlags.UseComponentsMax;
        }

        [Header("Output resources")]
        [Tooltip("Optional render texture to copy motion vector data to, for additional post-processing after upscaling.")]
        public RenderTexture outputMotionVectors;

        private Fsr2Context _context;
        private Vector2Int _renderSize;
        private Vector2Int _displaySize;
        private bool _reset;
        
        private readonly Fsr2.DispatchDescription _dispatchDescription = new Fsr2.DispatchDescription();
        private readonly Fsr2.GenerateReactiveDescription _genReactiveDescription = new Fsr2.GenerateReactiveDescription();

        private Fsr2ImageEffectHelper _helper;
        
        private Camera _renderCamera;
        private RenderTexture _originalRenderTarget;
        private DepthTextureMode _originalDepthTextureMode;
        private Rect _originalRect;

        private Fsr2.QualityMode _prevQualityMode;
        private Vector2Int _prevDisplaySize;
        private bool _prevGenReactiveMask;

        private CommandBuffer _dispatchCommandBuffer;
        private CommandBuffer _opaqueInputCommandBuffer;

        private Material _copyWithDepthMaterial;

        private void OnEnable()
        {
            // Set up the original camera to output all of the required FSR2 input resources at the desired resolution
            _renderCamera = GetComponent<Camera>();
            _originalRenderTarget = _renderCamera.targetTexture;
            _originalDepthTextureMode = _renderCamera.depthTextureMode;
            _renderCamera.targetTexture = null;     // Clear the camera's target texture so we can fully control how the output gets written
            _renderCamera.depthTextureMode = _originalDepthTextureMode | DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            
            // Determine the desired rendering and display resolutions
            _displaySize = GetDisplaySize();
            Fsr2.GetRenderResolutionFromQualityMode(out var renderWidth, out var renderHeight, _displaySize.x, _displaySize.y, qualityMode);
            _renderSize = new Vector2Int(renderWidth, renderHeight);
            
            // Apply a mipmap bias so that textures retain their sharpness
            float biasOffset = Fsr2.GetMipmapBiasOffset(_renderSize.x, _displaySize.x);
            if (!float.IsNaN(biasOffset))
            {
                Callbacks.ApplyMipmapBias(biasOffset);
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("FSR2 requires compute shader support!");
                enabled = false;
                return;
            }

            if (_renderSize.x == 0 || _renderSize.y == 0)
            {
                Debug.LogError($"FSR2 render size is invalid: {_renderSize.x}x{_renderSize.y}. Please check your screen resolution and camera viewport parameters.");
                enabled = false;
                return;
            }
            
            _helper = GetComponent<Fsr2ImageEffectHelper>();
            
            // Initialize FSR2 context
            Fsr2.InitializationFlags flags = 0;
            if (_renderCamera.allowHDR) flags |= Fsr2.InitializationFlags.EnableHighDynamicRange;
            if (enableFP16) flags |= Fsr2.InitializationFlags.EnableFP16Usage;
            if (enableAutoExposure) flags |= Fsr2.InitializationFlags.EnableAutoExposure;

            _context = Fsr2.CreateContext(_displaySize, _renderSize, Callbacks, flags);

            _dispatchCommandBuffer = new CommandBuffer { name = "FSR2 Dispatch" };

            // Create command buffers to bind the camera's output at the right moments in the render loop
            _opaqueInputCommandBuffer = new CommandBuffer { name = "FSR2 Opaque Input" };
            _opaqueInputCommandBuffer.GetTemporaryRT(Fsr2ShaderIDs.SrvOpaqueOnly, _renderSize.x, _renderSize.y, 0, default, GetDefaultFormat());
            _opaqueInputCommandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, Fsr2ShaderIDs.SrvOpaqueOnly);

            if (autoGenerateReactiveMask)
            {
                _renderCamera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _opaqueInputCommandBuffer);
            }

            _copyWithDepthMaterial = new Material(Shader.Find("Hidden/BlitCopyWithDepth"));

            _prevDisplaySize = _displaySize;
            _prevQualityMode = qualityMode;
            _prevGenReactiveMask = autoGenerateReactiveMask;
        }

        private void OnDisable()
        {
            // Undo the current mipmap bias offset
            float biasOffset = Fsr2.GetMipmapBiasOffset(_renderSize.x, _prevDisplaySize.x);
            if (!float.IsNaN(biasOffset))
            {
                Callbacks.ApplyMipmapBias(-biasOffset);
            }

            // Restore the camera's original state
            _renderCamera.depthTextureMode = _originalDepthTextureMode;
            _renderCamera.targetTexture = _originalRenderTarget;

            if (_copyWithDepthMaterial != null)
            {
                Destroy(_copyWithDepthMaterial);
                _copyWithDepthMaterial = null;
            }
            
            if (_opaqueInputCommandBuffer != null)
            {
                _renderCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _opaqueInputCommandBuffer);
                _opaqueInputCommandBuffer.Release();
                _opaqueInputCommandBuffer = null;
            }

            if (_dispatchCommandBuffer != null)
            {
                _dispatchCommandBuffer.Release();
                _dispatchCommandBuffer = null;
            }

            if (_context != null)
            {
                _context.Destroy();
                _context = null;
            }
        }

        private void Update()
        {
            var displaySize = GetDisplaySize();
            if (displaySize.x != _prevDisplaySize.x || displaySize.y != _prevDisplaySize.y || qualityMode != _prevQualityMode)
            {
                // Force all resources to be destroyed and recreated with the new settings
                OnDisable();
                OnEnable();
            }

            if (autoGenerateReactiveMask != _prevGenReactiveMask)
            {
                if (autoGenerateReactiveMask)
                    _renderCamera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _opaqueInputCommandBuffer);
                else
                    _renderCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _opaqueInputCommandBuffer);
                
                _prevGenReactiveMask = autoGenerateReactiveMask;
            }
        }

        public void Reset()
        {
            _reset = true;
        }

        private void LateUpdate()
        {
            // Remember the original camera viewport before we modify it in OnPreCull
            _originalRect = _renderCamera.rect;
        }

        private void OnPreCull()
        {
            if (_helper == null || !_helper.enabled)
            {
                // Render to a smaller portion of the screen by manipulating the camera's viewport rect
                _renderCamera.aspect = (_displaySize.x * _originalRect.width) / (_displaySize.y * _originalRect.height);
                _renderCamera.rect = new Rect(0, 0, _originalRect.width * _renderSize.x / _renderCamera.pixelWidth, _originalRect.height * _renderSize.y / _renderCamera.pixelHeight);
            }

            // Set up the parameters to auto-generate a reactive mask
            if (autoGenerateReactiveMask)
            {
                _genReactiveDescription.ColorOpaqueOnly = null;
                _genReactiveDescription.ColorPreUpscale = null;
                _genReactiveDescription.OutReactive = null;
                _genReactiveDescription.RenderSize = _renderSize;
                _genReactiveDescription.Scale = generateReactiveParameters.scale;
                _genReactiveDescription.CutoffThreshold = generateReactiveParameters.cutoffThreshold;
                _genReactiveDescription.BinaryValue = generateReactiveParameters.binaryValue;
                _genReactiveDescription.Flags = generateReactiveParameters.flags;
            }
            
            // Set up the main FSR2 dispatch parameters
            // The input and output textures are left blank here, as they are already being bound elsewhere in this source file
            _dispatchDescription.Color = null;
            _dispatchDescription.Depth = null;
            _dispatchDescription.MotionVectors = null;
            _dispatchDescription.Exposure = null;
            _dispatchDescription.Reactive = null;
            _dispatchDescription.TransparencyAndComposition = null;
            
            if (!enableAutoExposure && exposure != null) _dispatchDescription.Exposure = exposure;
            if (reactiveMask != null) _dispatchDescription.Reactive = reactiveMask;
            if (transparencyAndCompositionMask != null) _dispatchDescription.TransparencyAndComposition = transparencyAndCompositionMask;
            
            _dispatchDescription.Output = null;
            _dispatchDescription.PreExposure = preExposure;
            _dispatchDescription.EnableSharpening = performSharpenPass;
            _dispatchDescription.Sharpness = sharpness;
            _dispatchDescription.MotionVectorScale.x = -_renderSize.x;
            _dispatchDescription.MotionVectorScale.y = -_renderSize.y;
            _dispatchDescription.RenderSize = _renderSize;
            _dispatchDescription.FrameTimeDelta = Time.unscaledDeltaTime;
            _dispatchDescription.CameraNear = _renderCamera.nearClipPlane;
            _dispatchDescription.CameraFar = _renderCamera.farClipPlane;
            _dispatchDescription.CameraFovAngleVertical = _renderCamera.fieldOfView * Mathf.Deg2Rad;
            _dispatchDescription.ViewSpaceToMetersFactor = 1.0f; // 1 unit is 1 meter in Unity
            _dispatchDescription.Reset = _reset;
            _reset = false;

            if (SystemInfo.usesReversedZBuffer)
            {
                // Swap the near and far clip plane distances as FSR2 expects this when using inverted depth
                (_dispatchDescription.CameraNear, _dispatchDescription.CameraFar) = (_dispatchDescription.CameraFar, _dispatchDescription.CameraNear);
            }

            // Perform custom jittering of the camera's projection matrix according to FSR2's recipe
            int jitterPhaseCount = Fsr2.GetJitterPhaseCount(_renderSize.x, _displaySize.x);
            Fsr2.GetJitterOffset(out float jitterX, out float jitterY, Time.frameCount, jitterPhaseCount);

            _dispatchDescription.JitterOffset = new Vector2(jitterX, jitterY);

            jitterX = 2.0f * jitterX / _renderSize.x;
            jitterY = 2.0f * jitterY / _renderSize.y;

            var jitterTranslationMatrix = Matrix4x4.Translate(new Vector3(jitterX, jitterY, 0));
            _renderCamera.nonJitteredProjectionMatrix = _renderCamera.projectionMatrix;
            _renderCamera.projectionMatrix = jitterTranslationMatrix * _renderCamera.nonJitteredProjectionMatrix;
            _renderCamera.useJitteredProjectionMatrixForTransparentRendering = true;
        }
        
        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            // Restore the camera's viewport rect so we can output at full resolution
            _renderCamera.rect = _originalRect;
            _renderCamera.ResetProjectionMatrix();

            // Update the input resource descriptions
            _dispatchDescription.InputResourceSize = new Vector2Int(src.width, src.height);

            _dispatchCommandBuffer.Clear();
            _dispatchCommandBuffer.SetGlobalTexture(Fsr2ShaderIDs.SrvInputColor, BuiltinRenderTextureType.CameraTarget, RenderTextureSubElement.Color);
            _dispatchCommandBuffer.SetGlobalTexture(Fsr2ShaderIDs.SrvInputDepth, BuiltinRenderTextureType.CameraTarget, RenderTextureSubElement.Depth);
            _dispatchCommandBuffer.SetGlobalTexture(Fsr2ShaderIDs.SrvInputMotionVectors, BuiltinRenderTextureType.MotionVectors);

            if (autoGenerateReactiveMask)
            {
                _dispatchCommandBuffer.GetTemporaryRT(Fsr2ShaderIDs.UavAutoReactive, _renderSize.x, _renderSize.y, 0, default, GraphicsFormat.R8_UNorm, 1, true);
                _context.GenerateReactiveMask(_genReactiveDescription, _dispatchCommandBuffer);
                _dispatchCommandBuffer.ReleaseTemporaryRT(Fsr2ShaderIDs.SrvOpaqueOnly);
                
                _dispatchDescription.Reactive = Fsr2ShaderIDs.UavAutoReactive;
            }

            // We are rendering to the backbuffer, so we need a temporary render texture for FSR2 to output to
            _dispatchCommandBuffer.GetTemporaryRT(Fsr2ShaderIDs.UavUpscaledOutput, _displaySize.x, _displaySize.y, 0, default, GetDefaultFormat(), default, 1, true);

            _context.Dispatch(_dispatchDescription, _dispatchCommandBuffer);

            // Output the upscaled image
            if (_originalRenderTarget != null)
            {
                // Output to the camera target texture, passing through depth and motion vectors
                _dispatchCommandBuffer.SetGlobalTexture("_DepthTex", BuiltinRenderTextureType.CameraTarget, RenderTextureSubElement.Depth);
                _dispatchCommandBuffer.Blit(Fsr2ShaderIDs.UavUpscaledOutput, _originalRenderTarget, _copyWithDepthMaterial);
                if (outputMotionVectors != null)
                    _dispatchCommandBuffer.Blit(BuiltinRenderTextureType.MotionVectors, outputMotionVectors);
            }
            else
            {
                // Output directly to the backbuffer
                _dispatchCommandBuffer.Blit(Fsr2ShaderIDs.UavUpscaledOutput, dest);
            }
            
            _dispatchCommandBuffer.ReleaseTemporaryRT(Fsr2ShaderIDs.UavUpscaledOutput);

            if (autoGenerateReactiveMask)
            {
                _dispatchCommandBuffer.ReleaseTemporaryRT(Fsr2ShaderIDs.UavAutoReactive);
            }

            Graphics.ExecuteCommandBuffer(_dispatchCommandBuffer);

            // Shut up the Unity warning about not writing to the destination texture 
            RenderTexture.active = dest;
        }

        private RenderTextureFormat GetDefaultFormat()
        {
            if (_originalRenderTarget != null)
                return _originalRenderTarget.format;

            return _renderCamera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
        }

        private Vector2Int GetDisplaySize()
        {
            if (_originalRenderTarget != null)
                return new Vector2Int(_originalRenderTarget.width, _originalRenderTarget.height);
            
            return new Vector2Int(_renderCamera.pixelWidth, _renderCamera.pixelHeight);
        }
    }
}

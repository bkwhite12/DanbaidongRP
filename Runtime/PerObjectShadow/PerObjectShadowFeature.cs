using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Renders a shadow map for per Objects.
    /// TODO: Support RenderGraph.
    /// </summary>
    [DisallowMultipleRendererFeature("Per Object Shadow Feature")]
    [Tooltip("Per Object Shadow, render shadowmaps for every single object")]
    public class PerObjectShadowFeature : ScriptableRendererFeature
    {
        private static SharedObjectShadowEntityManager sharedObjectShadowEntityManager { get; } = new SharedObjectShadowEntityManager();


        // Serialized Fields
        //[SerializeField] private PerObjectShadowSettings m_Settings = new PerObjectShadowSettings();

        // Private Fields
        private bool m_RecreateSystems;
        private Light m_DirectLight;// We can't get lightdata before cameraPreCull, this stores last frame light.
        private PerObjectShadowCasterPass m_PerObjectShadowCasterPass = null;
        private PerObjectScreenSpaceShadowsPass m_PerObjectScreenSpaceShadowsPass = null;
        private Shadows m_volumeSettings;

        // Entities
        private ObjectShadowEntityManager m_ObjectShadowEntityManager;
        private ObjectShadowUpdateCachedSystem m_ObjectShadowUpdateCachedSystem;
        private ObjectShadowUpdateCullingGroupSystem m_ObjectShadowUpdateCullingGroupSystem;
        private ObjectShadowUpdateCulledSystem m_ObjectShadowUpdateCulledSystem;
        private ObjectShadowCreateDrawCallSystem m_ObjectShadowCreateDrawCallSystem;
        private ObjectShadowDrawSystem m_ObjectShadowDrawSystem;

        // Constants


        /// <inheritdoc/>
        public override void Create()
        {

            m_RecreateSystems = true;
        }

        private bool RecreateSystemsIfNeeded(ScriptableRenderer renderer, float maxDrawDistance)
        {
            if (!m_RecreateSystems)
                return true;

            if (m_ObjectShadowEntityManager == null)
            {
                m_ObjectShadowEntityManager = sharedObjectShadowEntityManager.Get();
            }

            m_ObjectShadowUpdateCachedSystem = new ObjectShadowUpdateCachedSystem(m_ObjectShadowEntityManager);
            m_ObjectShadowUpdateCulledSystem = new ObjectShadowUpdateCulledSystem(m_ObjectShadowEntityManager);
            m_ObjectShadowCreateDrawCallSystem = new ObjectShadowCreateDrawCallSystem(m_ObjectShadowEntityManager, maxDrawDistance);
            m_ObjectShadowUpdateCullingGroupSystem = new ObjectShadowUpdateCullingGroupSystem(m_ObjectShadowEntityManager, maxDrawDistance);
            m_ObjectShadowDrawSystem = new ObjectShadowDrawSystem(m_ObjectShadowEntityManager);

            m_PerObjectShadowCasterPass = new PerObjectShadowCasterPass(m_ObjectShadowCreateDrawCallSystem);
            m_PerObjectScreenSpaceShadowsPass = new PerObjectScreenSpaceShadowsPass(m_ObjectShadowDrawSystem);

            m_PerObjectShadowCasterPass.renderPassEvent = RenderPassEvent.BeforeRenderingShadows;
            m_PerObjectScreenSpaceShadowsPass.renderPassEvent = RenderPassEvent.BeforeRenderingDeferredLights;

            m_RecreateSystems = false;
            return true;
        }

        /// <inheritdoc/>
        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            if (cameraData.cameraType == CameraType.Preview)
                return;

            if (m_DirectLight == null)
                return;

            bool isSystemsValid = RecreateSystemsIfNeeded(renderer, cameraData.universalCameraData.maxPerObjectShadowDistance);
            if (!isSystemsValid)
                return;

            // Update Manager and Execute culling systems
            m_ObjectShadowEntityManager.Update();

            m_ObjectShadowUpdateCachedSystem.Execute(m_DirectLight);
            m_ObjectShadowUpdateCullingGroupSystem.Execute(cameraData.camera);

            //string chunksInfo = "Manager chunkCount: " + m_ObjectShadowEntityManager.chunkCount;
            //for (int i = 0; i < m_ObjectShadowEntityManager.chunkCount; i ++)
            //{
            //    chunksInfo += "\nEntityChunk" + i + ": count " + m_ObjectShadowEntityManager.entityChunks[i].count + " capcity " + m_ObjectShadowEntityManager.entityChunks[i].capacity;
            //}
            //Debug.Log(chunksInfo);
        }

        /// <inheritdoc/>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Exclude PreView camera
            if (renderingData.cameraData.cameraType == CameraType.Preview)
            {
                return;
            }

            // Directional mainLight check.
            {
                int shadowLightIndex = renderingData.lightData.mainLightIndex;
                if (shadowLightIndex == -1)
                    return;

                VisibleLight shadowLight = renderingData.lightData.visibleLights[shadowLightIndex];
                m_DirectLight = shadowLight.light;
                if (m_DirectLight.shadows == LightShadows.None)
                    return;

                if (shadowLight.lightType != LightType.Directional)
                {
                    Debug.LogWarning("Only directional lights are supported as main light.");
                }
            }

            // ObjectShadowSystem check
            bool isSystemsValid = RecreateSystemsIfNeeded(renderer, renderingData.cameraData.universalCameraData.maxPerObjectShadowDistance);
            if (!isSystemsValid)
                return;

            // Execute systems
            m_ObjectShadowUpdateCulledSystem.Execute();

            //string chunksInfo = "Manager chunkCount: " + m_ObjectShadowEntityManager.chunkCount;
            //for (int i = 0; i < m_ObjectShadowEntityManager.chunkCount; i++)
            //{
            //    chunksInfo += "\nCulledChunk" + i + ": count " + m_ObjectShadowEntityManager.culledChunks[i].count + " visible[" + m_ObjectShadowEntityManager.culledChunks[i].visibleObjectShadowCount + "] ";
            //    for (int index = 0; index < m_ObjectShadowEntityManager.culledChunks[i].visibleObjectShadowCount; index++)
            //    {
            //        chunksInfo += " " + m_ObjectShadowEntityManager.culledChunks[i].visibleObjectShadowIndexArray[index];
            //    }

            //}
            //Debug.Log(chunksInfo);

            int maxVisibleCountPerChunk = 0;
            for (int i = 0; i < m_ObjectShadowEntityManager.chunkCount; i++)
            {
                maxVisibleCountPerChunk = Mathf.Max(maxVisibleCountPerChunk, m_ObjectShadowEntityManager.culledChunks[i].visibleObjectShadowCount);
            }
            // Exist when no visible entity
            if (maxVisibleCountPerChunk == 0)
            {
                //ClearRenderingState(universalRenderingData.commandBuffer);
                return;
            }

            var stack = VolumeManager.instance.stack;
            m_volumeSettings = stack.GetComponent<Shadows>();
            if (m_volumeSettings == null)
                return;

            if (m_PerObjectShadowCasterPass.Setup(ref renderingData, m_ObjectShadowEntityManager, m_volumeSettings))
            {
                renderer.EnqueuePass(m_PerObjectShadowCasterPass);

                if (m_PerObjectScreenSpaceShadowsPass.Setup(m_volumeSettings))
                    renderer.EnqueuePass(m_PerObjectScreenSpaceShadowsPass);
            }
        }

        /// <summary>
        /// Clear pass keywords.
        /// </summary>
        /// <param name="cmd"></param>
        private void ClearRenderingState(CommandBuffer cmd)
        {
            // Note that we clear at OnCameraCleanup at PerObjectScreenSpaceShadowsPass. Like URP SetKeyword do.
            //m_PerObjectScreenSpaceShadowsPass.ClearRenderingState(cmd);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            m_PerObjectShadowCasterPass?.Dispose();
            m_PerObjectShadowCasterPass = null;

            m_PerObjectScreenSpaceShadowsPass?.Dispose();
            m_PerObjectScreenSpaceShadowsPass = null;

            if (m_ObjectShadowEntityManager != null)
            {
                m_ObjectShadowEntityManager = null;
                sharedObjectShadowEntityManager.Release(m_ObjectShadowEntityManager);
            }
        }

    }

}
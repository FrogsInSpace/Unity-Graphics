using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.HighDefinition
{
    /// <summary>
    /// Base class to inherit to create custom post process volume editors.
    /// </summary>
    [CanEditMultipleObjects]
    [CustomEditor(typeof(CustomPostProcessVolumeComponent), true)]
    public class CustomPostProcessVolumeComponentEditor : VolumeComponentEditor
    {
        internal static class Styles
        {
            public const string helpBoxLabel = "Custom Post Process Orders";
            public const string helpBoxMessage = "This Custom Post Process is not registered in ProjectSettings > Graphics.";
        }

        /// <summary>
        /// Unity calls this method each time it re-draws the Inspector.
        /// </summary>
        /// <remarks>
        /// You can safely override this method and not call <c>base.OnInspectorGUI()</c> unless you
        /// want Unity to display all the properties from the <see cref="VolumeComponent"/>
        /// automatically.
        /// </remarks>
        public override void OnInspectorGUI()
        {
            if (GraphicsSettings.TryGetRenderPipelineSettings<CustomPostProcessOrdersSettings>(out var customPPOrders) && customPPOrders.IsCustomPostProcessRegistered(target.GetType()))
            {
                base.OnInspectorGUI();
            }
            else
            {
                HDEditorUtils.GlobalSettingsHelpBox(Styles.helpBoxMessage, MessageType.Error, Styles.helpBoxLabel);
            }
        }
    }
}

#if !UNITY_EDITOR_OSX || MAC_FORCE_TESTS
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

using NUnit.Framework;

using UnityEditor.VFX.Block;
using UnityEditor.VFX.UI;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.VFX;

namespace UnityEditor.VFX.Test
{
    [TestFixture]
    class CustomHLSLBlockTest
    {
        const string defaultHlslCode =
            "void TestFunction(inout VFXAttributes attributes, in float3 offset, in float speed)\n" +
            "{\n" +
            "  attributes.position += offset;\n" +
            "  attributes.velocity = float3(0, 0, speed);\n" +
            "}";

        [OneTimeSetUp]
        public void Setup()
        {
            VFXViewWindow.GetAllWindows().ToList().ForEach(x => x.Close());
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            VFXViewWindow.GetAllWindows().ToList().ForEach(x => x.Close());
            VFXTestCommon.DeleteAllTemporaryGraph();
        }

        [Test]
        public void Check_CustomHLSL_Block_Generated_Code()
        {
            // Arrange
            var blockName = "AutoTest";
            var hlslBlock = CreateCustomHLSLBlock();
            hlslBlock.SetSettingValue("m_HLSLCode", defaultHlslCode);
            hlslBlock.SetSettingValue("m_BlockName", blockName);

            // Act
            hlslBlock.Invalidate(VFXModel.InvalidationCause.kSettingChanged);

            // Assert
            var function = GetFunction(hlslBlock);

            var expectedSource = "VFXAttributes att = (VFXAttributes)0;\r\n" +
                                      "att.position = position;\r\n" +
                                      "att.velocity = velocity;\r\n" +
                                     $"{function.GetNameWithHashCode()}(att, offset, speed);\r\n" +
                                      "position = att.position;\r\n" +
                                      "velocity = att.velocity;";
            Assert.AreEqual(expectedSource, hlslBlock.source);
            Assert.IsEmpty(hlslBlock.includes);
            Assert.AreEqual(blockName, hlslBlock.name);

            var expectedCustomCode = $"void {function.GetNameWithHashCode()}(inout VFXAttributes attributes, in float3 offset, in float speed)\r\n" +
                                           "{\n" +
                                           "  attributes.position += offset;\n" +
                                           "  attributes.velocity = float3(0, 0, speed);\n" +
                                           "}\r\n";
            Assert.AreEqual(expectedCustomCode, hlslBlock.customCode);

            var expectedAttributes = new[] { "position", "velocity" };
            Assert.AreEqual(expectedAttributes, hlslBlock.attributes.Select(x => x.attrib.name).ToArray());
        }

        [Test]
        public void Check_CustomHLSL_Block_Use_Shader_File()
        {
            // Arrange
            var blockName = "AutoTest";
            var shaderInclude = CreateShaderFile(defaultHlslCode, out var shaderIncludePath);
            var hlslBlock = CreateCustomHLSLBlock();
            hlslBlock.SetSettingValue("m_ShaderFile", shaderInclude);
            hlslBlock.SetSettingValue("m_BlockName", blockName);

            // Act
            hlslBlock.Invalidate(VFXModel.InvalidationCause.kSettingChanged);

            // Assert
            var expectedSource = "VFXAttributes att = (VFXAttributes)0;\r\n" +
                                 "att.position = position;\r\n" +
                                 "att.velocity = velocity;\r\n" +
                                 "TestFunction(att, offset, speed);\r\n" +
                                 "position = att.position;\r\n" +
                                 "velocity = att.velocity;";
            Assert.AreEqual(expectedSource, hlslBlock.source);
            var includes = hlslBlock.includes.ToArray();
            Assert.AreEqual(1, includes.Length);
            Assert.AreEqual(shaderIncludePath, includes[0]);

            Assert.AreEqual(blockName, hlslBlock.name);
            Assert.AreEqual(string.Empty, hlslBlock.customCode);

            var expectedAttributes = new[] { "position", "velocity" };
            Assert.AreEqual(expectedAttributes, hlslBlock.attributes.Select(x => x.attrib.name).ToArray());
        }

        [Test]
        public void Check_CustomHLSL_Block_Attribute_Access_Mode()
        {
            // Arrange
            var hlslBlock = CreateCustomHLSLBlock();
            hlslBlock.SetSettingValue("m_HLSLCode", defaultHlslCode);

            // Act
            hlslBlock.Invalidate(VFXModel.InvalidationCause.kSettingChanged);

            // Assert
            var expectedAttributes = new[] { VFXAttributeMode.ReadWrite, VFXAttributeMode.Write };
            Assert.AreEqual(expectedAttributes, hlslBlock.attributes.Select(x => x.mode).ToArray());
        }

        [Test]
        public void Check_CustomHLSL_Block_Attribute_Read_Access()
        {
            // Arrange
            var hlslCode =
                "void TestFunction(inout VFXAttributes attributes, in float3 offset, in float speedFactor)\n" +
                "{\n" +
                    "/* 01 */ float f = attributes.lifetime;\n" +
                    "/* 02 */ float g = Function(attributes.scaleX);\n" +
                    "/* 03 */ float v = Function(attributes.position.x);\n" +
                    "/* 04 */ float i = Function2(attributes.velocity.x, attributes.scaleY);\n" +
                    "/* 05 */ if (attributes.scaleZ > 0);\n" +
                    "/* 06 */ if (attributes.seed < 0);\n" +
                    "/* 07 */ if (attributes.age == 0);\n" +
                    "/* 08 */ if (attributes.texIndex == 0 && attributes.direction.z > 0);\n" +
                    "/* 09 */ if (4 < attributes.particleId);\n" +
                    "/* 10 */ if (attributes.alive);\n" +
                    "/* 11 */ if (4 <= attributes.pivotX);\n" +
                    "/* 12 */ if (4 >= attributes.pivotY);\n" +
                "}";

            var hlslBlock = CreateCustomHLSLBlock();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            // Act
            hlslBlock.Invalidate(VFXModel.InvalidationCause.kSettingChanged);

            // Assert
            Assert.AreEqual(14, hlslBlock.attributes.Count());
            var expectedAttributes = new[] { "lifetime", "scaleX", "position", "velocity", "scaleY", "scaleZ", "seed", "age", "texIndex", "direction", "particleId", "alive", "pivotX", "pivotY" };
            CollectionAssert.AreEquivalent(expectedAttributes, hlslBlock.attributes.Select(x => x.attrib.name));
            Assert.IsTrue(hlslBlock.attributes.All(x => x.mode == VFXAttributeMode.Read));
        }

        [Test]
        public void Check_CustomHLSL_Block_Attribute_Write_Access()
        {
            // Arrange
            var hlslCode =
                "void TestFunction(inout VFXAttributes attributes, in float3 offset, in float speedFactor)\n" +
                "{\n" +
                    "/* 01 */ attributes.position = float3(1, 2, 3);\n" +
                    "/* 02 */ attributes.direction.x = 4;\n" +
                    "/* 03 */ attributes.velocity = attributes.targetPosition = float3(0, 0, 0);\n" +
                "}";

            var hlslBlock = CreateCustomHLSLBlock();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            // Act
            hlslBlock.Invalidate(VFXModel.InvalidationCause.kSettingChanged);

            // Assert
            Assert.AreEqual(4, hlslBlock.attributes.Count());
            var expectedAttributes = new[] { "position", "velocity", "targetPosition", "direction" };
            CollectionAssert.AreEquivalent(expectedAttributes, hlslBlock.attributes.Select(x => x.attrib.name));
            Assert.IsTrue(hlslBlock.attributes.All(x => x.mode == VFXAttributeMode.Write));
        }

        [Test]
        public void Check_CustomHLSL_Block_Attribute_ReadWrite_Access()
        {
            // Arrange
            var hlslCode =
                "void TestFunction(inout VFXAttributes attributes, in float3 offset, in float speedFactor)\n" +
                "{\n" +
                    "/* 01 */ attributes.lifetime += 4;\n" +
                    "/* 02 */ attributes.scaleX -= 4;\n" +
                    "/* 03 */ attributes.scaleY *= 4;\n" +
                    "/* 04 */ attributes.scaleZ /= 4;\n" +
                    "/* 05 */ attributes.angleX %= 4;\n" +
                    "/* 06 */ attributes.angleY++;\n" +
                    "/* 07 */ attributes.angleZ--;\n" +
                    "/* 08 */ attributes.pivotX <<= 1;\n" +
                    "/* 09 */ attributes.pivotY >>= 1;\n" +
                    // Todo: Not detected ??
                    //"/* 10 */ ++attributes.AngularVelocityX;\n" +
                    //"/* 11 */ --attributes.AngularVelocityY;\n" +
                "}";

            var hlslBlock = CreateCustomHLSLBlock();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            // Act
            hlslBlock.Invalidate(VFXModel.InvalidationCause.kSettingChanged);

            // Assert
            Assert.AreEqual(9, hlslBlock.attributes.Count());
            var expectedAttributes = new[] { "lifetime", "scaleX", "scaleY", "scaleZ", "angleX", "angleY", "angleZ", "pivotX", "pivotY", /*"AngularVelocityX", "AngularVelocityY"*/ };
            CollectionAssert.AreEquivalent(expectedAttributes, hlslBlock.attributes.Select(x => x.attrib.name));
            Assert.IsTrue(hlslBlock.attributes.All(x => x.mode == VFXAttributeMode.ReadWrite));
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Return_Type_Not_Void()
        {
            // Arrange
            var hlslCode =
                "float TestFunction(inout VFXAttributes attributes, in float3 offset, in float speedFactor)\n" +
                "{\n" +
                "}";

            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            var hasRegisteredError = false;
            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph, false /* this test expects shader errors */);
            view.graphView.errorManager.onRegisterError += (model, origin, error, errorType, description) => CheckErrorFeedback(
                model,
                errorType,
                description,
                hlslBlock,
                "HLSL function 'TestFunction' must return a 'void' type",
                VFXErrorType.Error,
                ref hasRegisteredError);

            yield return null;

            // Act
            VFXViewWindow.RefreshErrors(hlslBlock);

            // Assert
            Assert.IsTrue(hasRegisteredError);
        }

        public enum Check_CustomHLSL_Block_Works_In_Context_Case
        {
            Initialize,
            Update,
            Output,
            OutputSG
        }

        public static Array k_Check_CustomHLSL_Block_Works_In_Context_Case = Enum.GetValues(typeof(Check_CustomHLSL_Block_Works_In_Context_Case));

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Works_In_Context([ValueSource(nameof(k_Check_CustomHLSL_Block_Works_In_Context_Case))] Check_CustomHLSL_Block_Works_In_Context_Case target)
        {
            // Arrange
            var hlslCode =
@"void FindMe_In_Generated_Source(inout VFXAttributes attributes)
{
    attributes.color *= length(attributes.position);
}";

            VFXContextType contextType;
            switch (target)
            {
                case Check_CustomHLSL_Block_Works_In_Context_Case.Initialize: contextType = VFXContextType.Init; break;
                case Check_CustomHLSL_Block_Works_In_Context_Case.Update: contextType = VFXContextType.Update; break;
                case Check_CustomHLSL_Block_Works_In_Context_Case.Output:
                case Check_CustomHLSL_Block_Works_In_Context_Case.OutputSG: contextType = VFXContextType.Output; break;
                default: throw new NotImplementedException();
            }

            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);
            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph, true, contextType);

            if (target == Check_CustomHLSL_Block_Works_In_Context_Case.OutputSG)
            {
                var previousOutput = graph.children.OfType<VFXContext>().Single(x => x.contextType == VFXContextType.Output);
                previousOutput.RemoveChild(hlslBlock);
                var sgOutput = ScriptableObject.CreateInstance<VFXComposedParticleOutput>();
                sgOutput.SetSettingValue("m_Topology", new ParticleTopologyPlanarPrimitive());
                sgOutput.SetSettingValue("m_Shading", new ParticleShadingShaderGraph());
                sgOutput.AddChild(hlslBlock);

                var parentContext = previousOutput.inputFlowSlot.First().link.First().context;
                previousOutput.UnlinkAll();
                sgOutput.LinkFrom(parentContext);
                graph.AddChild(sgOutput);
            }

            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(graph));

            bool foundCustomFunction = false;
            var vfx = graph.visualEffectResource;
            for (int sourceIndex = 0; sourceIndex < vfx.GetShaderSourceCount(); ++sourceIndex)
            {
                var source = vfx.GetShaderSource(sourceIndex);
                if (source.Contains("FindMe_In_Generated_Source", StringComparison.InvariantCulture))
                {
                    foundCustomFunction = true;
                    break;
                }
            }
            Assert.IsTrue(foundCustomFunction);

            yield return null;
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_No_Function()
        {
            // Arrange
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", "toto");

            var hasRegisteredError = false;
            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph, false /* this test expects shader errors */);
            view.graphView.errorManager.onRegisterError += (model, origin, error, errorType, description) => CheckErrorFeedback(
                model,
                errorType,
                description,
                hlslBlock,
                "No valid HLSL function has been provided. You should write at least one function that returns a value",
                VFXErrorType.Error,
                ref hasRegisteredError);

            yield return null;

            // Act
            VFXViewWindow.RefreshErrors(hlslBlock);

            // Assert
            Assert.IsTrue(hasRegisteredError);
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Twice_Same_Function_Name()
        {
            // Arrange
            var functionName = "TestFunction";
            var hlslCode =
                $"void {functionName}(inout VFXAttributes attributes, in float3 offset, in float speedFactor)\n" +
                "{\n" +
                "attributes.size = 1;\n" +
                "}\n" +
                $"void {functionName}(inout VFXAttributes attributes, in float param)\n" +
                "{\n" +
                "attributes.alive = false;\n" +
                "}";
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            var hasRegisteredError = false;
            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph);
            view.graphView.errorManager.onRegisterError += (model, origin, error, errorType, description) => CheckErrorFeedback(
                model,
                errorType,
                description,
                hlslBlock,
                $"Multiple functions with same name '{functionName}' are declared, only the first one can be selected",
                VFXErrorType.Warning,
                ref hasRegisteredError);

            yield return null;

            // Act
            VFXViewWindow.RefreshErrors(hlslBlock);

            // Assert
            Assert.IsTrue(hasRegisteredError);
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Unknown_Parameter_Type()
        {
            // Arrange
            var parameterType = "xxxx";
            var hlslCode =
                $"void TestFunction(inout VFXAttributes attributes, in {parameterType} offset)\n" +
                "{\n" +
                "}";
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            var hasRegisteredError = false;
            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph, false /* this test expects shader errors */);
            view.graphView.errorManager.onRegisterError += (model, origin, error, errorType, description) => CheckErrorFeedback(
                model,
                errorType,
                description,
                hlslBlock,
                $"Unknown parameter type '{parameterType}'",
                VFXErrorType.Error,
                ref hasRegisteredError);

            yield return null;

            // Act
            VFXViewWindow.RefreshErrors(hlslBlock);

            // Assert
            Assert.IsTrue(hasRegisteredError);
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Texture2D_Type_Used()
        {
            // Arrange
            var paramName = "texture";
            var hlslCode =
                $"void TestFunction(inout VFXAttributes attributes, in Texture2D {paramName})\n" +
                "{\n" +
                "}";
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            var hasRegisteredError = false;
            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph, false /* this test expects shader errors */);
            view.graphView.errorManager.onRegisterError += (model, origin, error, errorType, description) => CheckErrorFeedback(
                model,
                errorType,
                description,
                hlslBlock,
                $"The function parameter '{paramName}' is of type Texture2D.\nPlease use VFXSampler2D type instead (see documentation)",
                VFXErrorType.Error,
                ref hasRegisteredError);

            yield return null;

            // Act
            VFXViewWindow.RefreshErrors(hlslBlock);

            // Assert
            Assert.IsTrue(hasRegisteredError);
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Missing_VFXAttributes()
        {
            // Arrange
            var hlslCode =
                "void TestFunction(in float param)\n" +
                "{\n" +
                "    attributes.position = float3(param, param, param);\n" +
                "}";
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            var hasRegisteredError = false;
            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph, false /* this test expects shader errors */);
            view.graphView.errorManager.onRegisterError += (model, origin, error, errorType, description) => CheckErrorFeedback(
                model,
                errorType,
                description,
                hlslBlock,
                "Missing `VFXAttributes attributes` as function's parameter.\nNeeded because your code access (read or write) to at least one attribute.\nIt has been automatically fixed for you",
                VFXErrorType.Warning,
                ref hasRegisteredError);

            yield return null;

            // Act
            VFXViewWindow.RefreshErrors(hlslBlock);

            // Assert
            Assert.IsTrue(hasRegisteredError);
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_VFXAttributes_Wrong_Access_Modifier()
        {
            // Arrange
            var hlslCode =
                "void TestFunction(in VFXAttributes attributes, in float param)\n" +
                "{\n" +
                "    attributes.position = float3(param, param, param);\n" +
                "}";
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            var hasRegisteredError = false;
            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph);
            view.graphView.errorManager.onRegisterError += (model, origin, error, errorType, description) => CheckErrorFeedback(
                model,
                errorType,
                description,
                hlslBlock,
                "Missing `inout` access modifier before the VFXAttributes type.\nNeeded because your code writes to at least one attribute.",
                VFXErrorType.Error,
                ref hasRegisteredError);

            yield return null;

            // Act
            VFXViewWindow.RefreshErrors(hlslBlock);

            // Assert
            Assert.IsTrue(hasRegisteredError);
        }

        [Test]
        public void Check_CustomHLSL_Block_Includes()
        {
            // Arrange
            var includeFilePath = "path/to/include/file.hlsl";
            var hlslCode =
                $"#include \"{includeFilePath}\"\n" +
                "void TestFunction(in VFXAttributes attributes, in float param)\n" +
                "{\n" +
                "    attributes.position = float3(param, param, param);\n" +
                "}";
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph, false /* this test expects shader errors */);

            // Act
            var includes = hlslBlock.includes.ToArray();

            // Assert
            Assert.AreEqual(1, includes.Length);
            Assert.AreEqual(includeFilePath, includes[0]);
            // Delete test asset immediately because it won't compile because the included hlsl file do not exists
            var path = AssetDatabase.GetAssetPath(graph);
            AssetDatabase.DeleteAsset(path);
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Compiles()
        {
            // Arrange
            var hlslCode =
                "void TestFunction(in VFXAttributes attributes, in float param)\n" +
                "{\n" +
                "    attributes.position = float3(param, param, param);\n" +
                "}";
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph);

            // Act
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(graph));
            yield return null;

            // Assert
            Assert.Pass("No exception should be raised");
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Use_CustomAttribute()
        {
            // Arrange
            var hlslCode =
                "void TestFunction(in VFXAttributes attributes, in float param)\n" +
                "{\n" +
                "    attributes.position = attributes.custom * param;\n" +
                "}";
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            hlslBlock.SetSettingValue("m_HLSLCode", hlslCode);

            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph);
            Assert.IsTrue(graph.TryAddCustomAttribute("custom", VFXValueType.Float3, null, false, out var attribute));

            // Act
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(graph));
            yield return null;

            // Assert
            Assert.Pass("No exception should be raised");
        }

        [UnityTest]
        public IEnumerator Check_CustomHLSL_Block_Use_CustomAttribute_HLSL_File()
        {
            // Arrange
            var hlslCode =
                "void TestFunction(in VFXAttributes attributes, in float param)\n" +
                "{\n" +
                "    attributes.position = attributes.custom * param;\n" +
                "}";

            Directory.CreateDirectory(VFXTestCommon.tempBasePath);
            var hlslFilePath = $"{VFXTestCommon.tempBasePath}/mycode.hlsl";
            File.WriteAllText(hlslFilePath, hlslCode);
            AssetDatabase.ImportAsset(hlslFilePath);

            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            var shaderInclude = AssetDatabase.LoadAssetAtPath<ShaderInclude>(hlslFilePath);
            hlslBlock.SetSettingValue("m_ShaderFile", shaderInclude);

            MakeSimpleGraphWithCustomHLSL(hlslBlock, out var view, out var graph);
            Assert.IsTrue(graph.TryAddCustomAttribute("custom", VFXValueType.Float3, null, false, out var attribute));

            // Act
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(graph));
            yield return null;

            // Assert
            Assert.Pass("No exception should be raised");
        }

        internal static ShaderInclude CreateShaderFile(string hlslCode, out string destinationPath)
        {
            destinationPath = Path.Combine(VFXTestCommon.tempBasePath, Guid.NewGuid() + ".hlsl");
            Directory.CreateDirectory(VFXTestCommon.tempBasePath);
            File.WriteAllText(destinationPath, hlslCode);
            AssetDatabase.ImportAsset(destinationPath);
            var shaderInclude = (ShaderInclude)AssetDatabase.LoadAssetAtPath(destinationPath, typeof(ShaderInclude));

            return shaderInclude;
        }

        internal static void CheckErrorFeedback(VFXModel model, VFXErrorType errorType, string description, VFXModel expectedModel, string expectedError, VFXErrorType expectedErrorType, ref bool hasRegisteredError)
        {
            if (hasRegisteredError)
                return;
            // Assert
            hasRegisteredError = expectedModel == model
                                 && expectedError == description
                                 && expectedErrorType == errorType;
        }

        internal static HLSLFunction GetFunction(VFXModel hlslOperator)
        {
            var fieldInfo = hlslOperator.GetType().GetField("m_Function", BindingFlags.Instance | BindingFlags.NonPublic);
            return (HLSLFunction)fieldInfo.GetValue(hlslOperator);
        }

        private void MakeSimpleGraphWithCustomHLSL(CustomHLSL hlslBlock, out VFXViewWindow view, out VFXGraph graph, bool compilable = true, VFXContextType targetContextType = VFXContextType.Update)
        {
            graph = VFXTestCommon.CreateGraph_And_System();
            view = VFXViewWindow.GetWindow(graph, true, true);
            view.LoadResource(graph.visualEffectResource);

            if (!compilable)
            {
                var outputContext = graph.children.OfType<VFXContext>().Single(x => x.contextType == VFXContextType.Output);
                outputContext.UnlinkAll();
            }

            var targetContext = graph.children.OfType<VFXContext>().Single(x => x.contextType == targetContextType);
            targetContext.AddChild(hlslBlock);

            view.graphView.OnSave();
        }

        private CustomHLSL CreateCustomHLSLBlock()
        {
            var graph = ScriptableObject.CreateInstance<VFXGraph>();
            var updateContext = ScriptableObject.CreateInstance<VFXBasicUpdate>();
            var hlslBlock = ScriptableObject.CreateInstance<CustomHLSL>();
            graph.AddChild(updateContext);
            updateContext.AddChild(hlslBlock);

            return hlslBlock;
        }
    }
}
#endif

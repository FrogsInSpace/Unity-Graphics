using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEditor.VFX.Block
{
    class PositionSDF : PositionShapeBase
    {
        public class InputProperties
        {
            [Tooltip("Sets the Signed Distance Field to sample from.")]
            public Texture3D SDF = VFXResources.defaultResources.signedDistanceField;
            [Tooltip("Sets the transform with which to position, scale, or rotate the field.")]
            public OrientedBox FieldTransform = OrientedBox.defaultValue;
        }

        public class CustomProperties
        {
            [Range(0, 1), Tooltip("When using customized emission, control the position around the arc to emit particles from.")]
            public float ArcSequencer = 0.0f;
        }

        public override IEnumerable<VFXNamedExpression> GetParameters(PositionShape positionBase, List<VFXNamedExpression> allSlots)
        {
            VFXExpression transform = null, sdf = null;
            foreach (var e in allSlots)
            {
                if (e.name == "FieldTransform")
                {
                    transform = e.exp;
                    yield return e;
                }

                if (e.name == "SDF")
                {
                    sdf = e.exp;
                    yield return e;
                }

                if (e.name == "ArcSequencer" ||
                    e.name == "Thickness")
                    yield return e;
            }

            var extents = new VFXNamedExpression(new VFXExpressionExtractScaleFromMatrix(transform), "extents");
            yield return new VFXNamedExpression(VFX.VFXOperatorUtility.Max3(extents.exp), "scalingFactor");
            yield return new VFXNamedExpression(new VFXExpressionInverseTRSMatrix(transform), "InvFieldTransform");
            var minDim = new VFXExpressionCastUintToFloat(VFX.VFXOperatorUtility.Min3(new VFXExpressionTextureHeight(sdf), new VFXExpressionTextureWidth(sdf), new VFXExpressionTextureDepth(sdf)));
            var gradStep = VFXValue.Constant(0.01f);  //kStep used in SampleSDFDerivativesFast and SampleSDFDerivatives
            var margin = VFXValue.Constant(0.5f) / minDim + gradStep + VFXValue.Constant(0.001f);
            yield return new VFXNamedExpression(VFXValue.Constant(0.5f) - margin, "projectionRayWithMargin");
            yield return new VFXNamedExpression(VFXValue.Constant(positionBase.projectionSteps), "n_steps");
        }

        public override string GetSource(PositionShape positionBase)
        {
            string outSource = @"float cosPhi = 2.0f * RAND - 1.0f;";
            if (positionBase.spawnMode == PositionBase.SpawnMode.Random)
                outSource += @"float theta = TWO_PI * RAND;";
            else
                outSource += @"float theta = TWO_PI * ArcSequencer;";
            switch (positionBase.positionMode)
            {
                case (PositionBase.PositionMode.Surface):
                    outSource += @" float Thickness = 0.0f;";
                    break;
                case (PositionBase.PositionMode.Volume):
                    outSource += @" float Thickness = scalingFactor;";
                    break;
                case (PositionBase.PositionMode.ThicknessRelative):
                    outSource += @" Thickness *= scalingFactor * 0.5f;";
                    break;
            }
            outSource += @"
//Initialize position within texture bounds
float2 sincosTheta;
sincos(theta, sincosTheta.x, sincosTheta.y);
sincosTheta *= sqrt(1.0f - cosPhi * cosPhi);
direction = float3(sincosTheta, cosPhi) *  sqrt(3)/2;
float maxDir = max(0.5f, max(abs(direction.x), max(abs(direction.y), abs(direction.z))));
direction = direction * (projectionRayWithMargin/maxDir);
float3 tPos = direction * pow(RAND, 1.0f/3.0f);
float3 coord = tPos + 0.5f;
float3 wPos, n, worldNormal;

for(uint proj_step=0; proj_step < n_steps; proj_step++){

    float dist = SampleSDF(SDF, coord);
    n = -normalize(SampleSDFDerivativesFast(SDF, coord, dist));
    dist *= scalingFactor;

    //Projection on surface/volume
    float3 delta;
    worldNormal = VFXSafeNormalize(mul(float4(n, 0), InvFieldTransform).xyz);
    if (dist > 0)
        delta = dist * worldNormal;
    else
    {
        delta = min(dist + Thickness, 0) * worldNormal;
    }

    wPos =  mul(FieldTransform, float4(tPos,1)).xyz + delta;
    tPos = mul(InvFieldTransform, float4(wPos,1)).xyz;
    coord = tPos + 0.5f;

}
position = wPos;
direction = -worldNormal;
                        ";
            if (positionBase.killOutliers)
            {
                outSource += @"

 float dist = SampleSDF(SDF, coord);
 if (dist * scalingFactor > 0.01)
    alive = false;
";
            }

            return outSource;
        }
    }
}

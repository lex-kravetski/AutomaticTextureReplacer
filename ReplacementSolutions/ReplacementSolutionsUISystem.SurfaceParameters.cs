using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Colossal.Json;
using Colossal.IO.AssetDatabase;
using Colossal.UI.Binding;
using Game.Areas;
using Game.Common;
using Game.Modding;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;


namespace AutomaticTextureReplacer
{
    public sealed partial class ReplacementSolutionsUISystem
    {        private void LoadSurfaceScaleValues(string json)
        {
            m_SurfaceScaleValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, float> value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value.Key) && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value))
                    {
                        m_SurfaceScaleValues[value.Key] = ClampSurfaceScale(value.Value);
                    }
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface scale values");
            }
        }

        private string SerializeSurfaceScaleValues()
        {
            return JSON.Dump(m_SurfaceScaleValues);
        }

        private void LoadSurfaceEdgeFadeValues(string json)
        {
            m_SurfaceEdgeFadeValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, float> value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value.Key))
                    {
                        float percent = Mathf.Clamp(value.Value, 0f, 10f);
                        if (percent > 0f)
                        {
                            m_SurfaceEdgeFadeValues[value.Key] = percent;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface edge fade values");
            }
        }

        private string SerializeSurfaceEdgeFadeValues()
        {
            return JSON.Dump(m_SurfaceEdgeFadeValues);
        }

        private void LoadSurfaceEdgeFadeEndValues(string json)
        {
            m_SurfaceEdgeFadeEndValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null) return;
                foreach (KeyValuePair<string, float> value in values)
                    if (!string.IsNullOrWhiteSpace(value.Key)) m_SurfaceEdgeFadeEndValues[value.Key] = Mathf.Clamp(value.Value, 0f, 100f);
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface edge fade end values");
            }
        }

        private string SerializeSurfaceEdgeFadeEndValues()
        {
            return JSON.Dump(m_SurfaceEdgeFadeEndValues);
        }

        private void LoadSurfaceEdgeNoiseValues(string json)
        {
            m_SurfaceEdgeNoiseValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null) return;
                foreach (KeyValuePair<string, float> value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value.Key))
                    {
                        m_SurfaceEdgeNoiseValues[value.Key] = Mathf.Clamp(value.Value, 0f, 10f);
                    }
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface edge noise values");
            }
        }

        private string SerializeSurfaceEdgeNoiseValues()
        {
            return JSON.Dump(m_SurfaceEdgeNoiseValues);
        }

        private void LoadSurfaceEdgeNoiseScaleValues(string json)
        {
            m_SurfaceEdgeNoiseScaleValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null) return;
                foreach (KeyValuePair<string, float> value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value.Key))
                        m_SurfaceEdgeNoiseScaleValues[value.Key] = Mathf.Clamp(value.Value, 0.01f, 10f);
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface edge noise scale values");
            }
        }

        private string SerializeSurfaceEdgeNoiseScaleValues()
        {
            return JSON.Dump(m_SurfaceEdgeNoiseScaleValues);
        }

        private void LoadSurfaceBaseColors(string json)
        {
            m_SurfaceBaseColors.Clear();
            try
            {
                Dictionary<string, string> values = JSON.MakeInto<Dictionary<string, string>>(JSON.Load(json ?? "{}"));
                if (values == null) return;
                foreach (KeyValuePair<string, string> value in values)
                {
                    string[] parts = (value.Value ?? string.Empty).Split(',');
                    if (parts.Length == 4 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) &&
                        float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) &&
                        float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b) &&
                        float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a))
                    {
                        m_SurfaceBaseColors[value.Key] = new Color(r, g, b, a);
                    }
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface base colors");
            }
        }

        private string SerializeSurfaceBaseColors()
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Color> value in m_SurfaceBaseColors)
            {
                values[value.Key] = string.Join(",", value.Value.r.ToString(System.Globalization.CultureInfo.InvariantCulture), value.Value.g.ToString(System.Globalization.CultureInfo.InvariantCulture), value.Value.b.ToString(System.Globalization.CultureInfo.InvariantCulture), value.Value.a.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return JSON.Dump(values);
        }

        private static readonly float[] s_SurfaceScaleStops = Enumerable.Range(0, 39)
            .Select(index => Mathf.Round((0.1f + index * 0.05f) * 100f) / 100f)
            .ToArray();

        private static float ClampSurfaceScale(float scale)
        {
            return Mathf.Clamp(scale, 1f, 50f);
        }

        private void OpenSurfaceScaleEditor(string request)
        {
            string surfaceId = request;
            string origin = "picker";
            int lastComma = request?.LastIndexOf(',') ?? -1;
            int yComma = lastComma > 0 ? request.LastIndexOf(',', lastComma - 1) : -1;
            int xComma = yComma > 0 ? request.LastIndexOf(',', yComma - 1) : -1;
            if (xComma > 0)
            {
                origin = request.Substring(lastComma + 1) == "main" ? "main" : "picker";
                surfaceId = request.Substring(0, xComma);
            }

            if (string.IsNullOrWhiteSpace(surfaceId) ||
                !TryFindSurfacePrefab(surfaceId, out _, out PrefabBase prefab, out RenderedArea renderedArea))
            {
                return;
            }

            m_ActiveSurfaceScaleId = surfaceId;
            m_ActiveSurfaceScaleHadSavedValue = m_SurfaceScaleValues.TryGetValue(surfaceId, out m_ActiveSurfaceScaleSavedValue);
            float defaultUvScale = m_SurfaceScaleBaseValues.TryGetValue(surfaceId, out float capturedUvScale)
                ? capturedUvScale
                : renderedArea.m_UVScale;
            m_ActiveSurfaceScaleBefore = m_ActiveSurfaceScaleHadSavedValue
                ? m_ActiveSurfaceScaleSavedValue
                : ClampSurfaceScale(Mathf.Abs(defaultUvScale) > 0.0001f ? 1f / defaultUvScale : 1f);
            m_ActiveSurfaceScaleDefault = ClampSurfaceScale(Mathf.Abs(defaultUvScale) > 0.0001f ? 1f / defaultUvScale : 1f);
            m_ActiveSurfaceScaleCurrent = m_ActiveSurfaceScaleBefore;
            m_ActiveSurfaceEdgeFadeHadSavedValue = m_SurfaceEdgeFadeValues.TryGetValue(surfaceId, out m_ActiveSurfaceEdgeFadeSavedValue);
            float2 defaultEdgeFade = m_SurfaceEdgeFadeBaseValues.TryGetValue(surfaceId, out float2 capturedEdgeFade)
                ? capturedEdgeFade
                : renderedArea.m_EdgeFadeRange;
            m_ActiveSurfaceEdgeFadeBefore = m_ActiveSurfaceEdgeFadeHadSavedValue
                ? m_ActiveSurfaceEdgeFadeSavedValue
                : 0f;
            m_ActiveSurfaceEdgeFadeCurrent = m_ActiveSurfaceEdgeFadeBefore;
            m_ActiveSurfaceEdgeFadeEndHadSavedValue = m_SurfaceEdgeFadeEndValues.TryGetValue(surfaceId, out m_ActiveSurfaceEdgeFadeEndSavedValue);
            m_ActiveSurfaceEdgeFadeEndBefore = m_ActiveSurfaceEdgeFadeEndHadSavedValue
                ? m_ActiveSurfaceEdgeFadeEndSavedValue
                : defaultEdgeFade.y;
            m_ActiveSurfaceEdgeFadeEndCurrent = m_ActiveSurfaceEdgeFadeEndBefore;
            m_ActiveSurfaceEdgeNoiseHadSavedValue = m_SurfaceEdgeNoiseValues.TryGetValue(surfaceId, out m_ActiveSurfaceEdgeNoiseSavedValue);
            float2 defaultEdgeNoise = m_SurfaceEdgeNoiseBaseValues.TryGetValue(surfaceId, out float2 capturedEdgeNoise)
                ? capturedEdgeNoise
                : renderedArea.m_EdgeNoise;
            // Edge noise is previewed from the prefab's default value. The
            // persisted custom value is intentionally not used to initialize
            // this editor control.
            m_ActiveSurfaceEdgeNoiseBefore = defaultEdgeNoise.x;
            m_ActiveSurfaceEdgeNoiseCurrent = m_ActiveSurfaceEdgeNoiseBefore;
            m_ActiveSurfaceEdgeNoiseDefault = m_ActiveSurfaceEdgeNoiseBefore;
            m_ActiveSurfaceEdgeNoiseScaleHadSavedValue = m_SurfaceEdgeNoiseScaleValues.TryGetValue(surfaceId, out m_ActiveSurfaceEdgeNoiseScaleSavedValue);
            m_ActiveSurfaceEdgeNoiseScaleBefore = m_ActiveSurfaceEdgeNoiseScaleHadSavedValue
                ? m_ActiveSurfaceEdgeNoiseScaleSavedValue
                : Mathf.Max(0.01f, defaultEdgeNoise.y);
            m_ActiveSurfaceEdgeNoiseScaleDefault = Mathf.Max(0.01f, defaultEdgeNoise.y);
            m_ActiveSurfaceEdgeNoiseScaleCurrent = m_ActiveSurfaceEdgeNoiseScaleBefore;
            m_ActiveSurfaceBaseColorHadSavedValue = m_SurfaceBaseColors.TryGetValue(surfaceId, out m_ActiveSurfaceBaseColorSavedValue);
            m_ActiveSurfaceBaseColorDefault = m_SurfaceBaseColorDefaults.TryGetValue(surfaceId, out Color capturedBaseColor)
                ? capturedBaseColor
                : renderedArea.m_BaseColor;
            m_ActiveSurfaceBaseColorBefore = m_ActiveSurfaceBaseColorHadSavedValue
                ? m_ActiveSurfaceBaseColorSavedValue
                : m_ActiveSurfaceBaseColorDefault;
            m_ActiveSurfaceBaseColorCurrent = m_ActiveSurfaceBaseColorBefore;
            Color.RGBToHSV(m_ActiveSurfaceBaseColorCurrent, out float baseHue, out float baseSaturation, out float baseValue);
            m_SurfaceScaleSurfaceId.Update(surfaceId);
            m_SurfaceScaleSurfaceName.Update(prefab.name ?? surfaceId);
            m_SurfaceScaleEditorOrigin.Update(origin);
            m_SurfaceScaleHundredths.Update(Mathf.RoundToInt(m_ActiveSurfaceScaleCurrent * 100f));
            m_SurfaceEdgeFadePercent.Update(m_ActiveSurfaceEdgeFadeCurrent);
            m_SurfaceEdgeFadeEndPercent.Update(m_ActiveSurfaceEdgeFadeEndCurrent);
            m_SurfaceEdgeNoisePercent.Update(m_ActiveSurfaceEdgeNoiseCurrent);
            m_SurfaceEdgeNoiseScale.Update(m_ActiveSurfaceEdgeNoiseScaleCurrent);
            // Zero is the UI sentinel for the prefab's default fade pair.
            m_SurfaceEdgeFadeDefault.Update(0f);
            m_SurfaceEdgeNoiseDefault.Update(defaultEdgeNoise.x);
            m_SurfaceEdgeNoiseScaleDefault.Update(m_ActiveSurfaceEdgeNoiseScaleDefault);
            m_SurfaceBaseColorHue.Update(baseHue);
            m_SurfaceBaseColorSaturation.Update(baseSaturation);
            m_SurfaceBaseColorBrightness.Update(baseValue);
            m_SurfaceBaseColorAlpha.Update(m_ActiveSurfaceBaseColorCurrent.a);
            m_SurfaceScaleEditorIsOpen = true;
            m_SurfaceScaleEditorOpen.Update(true);
        }

        private void SetSurfaceScaleEditorPosition(string value)
        {
            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return;
            m_SurfaceScaleEditorX.Update(x);
            m_SurfaceScaleEditorY.Update(y);
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfaceParametersX = x;
                Mod.Settings.SurfaceParametersY = y;
                Mod.Settings.ApplyAndSave();
            }
        }

        private float m_ActiveSurfaceScaleCurrent = 1f;
        private float m_ActiveSurfaceEdgeFadeBefore;
        private float m_ActiveSurfaceEdgeFadeSavedValue;
        private float m_ActiveSurfaceEdgeFadeCurrent;
        private bool m_ActiveSurfaceEdgeFadeHadSavedValue;
        private float m_ActiveSurfaceEdgeFadeEndBefore;
        private float m_ActiveSurfaceEdgeFadeEndSavedValue;
        private float m_ActiveSurfaceEdgeFadeEndCurrent;
        private bool m_ActiveSurfaceEdgeFadeEndHadSavedValue;
        private float m_ActiveSurfaceEdgeNoiseBefore;
        private float m_ActiveSurfaceEdgeNoiseSavedValue;
        private float m_ActiveSurfaceEdgeNoiseCurrent;
        private bool m_ActiveSurfaceEdgeNoiseHadSavedValue;
        private float m_ActiveSurfaceEdgeNoiseScaleBefore;
        private float m_ActiveSurfaceEdgeNoiseScaleDefault;
        private float m_ActiveSurfaceEdgeNoiseScaleSavedValue;
        private float m_ActiveSurfaceEdgeNoiseScaleCurrent;
        private bool m_ActiveSurfaceEdgeNoiseScaleHadSavedValue;
        private Color m_ActiveSurfaceBaseColorBefore;
        private Color m_ActiveSurfaceBaseColorDefault;
        private Color m_ActiveSurfaceBaseColorCurrent;
        private Color m_ActiveSurfaceBaseColorSavedValue;
        private bool m_ActiveSurfaceBaseColorHadSavedValue;

        private void SetSurfaceScalePreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !int.TryParse(value, out int hundredths))
            {
                return;
            }

            hundredths = Mathf.Clamp(hundredths, 100, 5000);
            Mod.Log.InfoFormat("Surface slider input: kind='Scale', raw='{0}', parsedHundredths={1}", value, hundredths);
            m_ActiveSurfaceScaleCurrent = ClampSurfaceScale(hundredths / 100f);
            SetSurfaceScaleValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceScaleHundredths.Update(Mathf.RoundToInt(m_ActiveSurfaceScaleCurrent * 100f));
            m_SurfaceScalesJson.Update(SerializeSurfaceScaleValues());
        }

        private void SetSurfaceEdgeFadePreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float percent))
            {
                return;
            }

            Mod.Log.InfoFormat("Surface slider input: kind='Edge fade', raw='{0}', parsed={1}, maximum={2}", value, percent, 20f);
            m_ActiveSurfaceEdgeFadeCurrent = Mathf.Clamp(percent, 0f, 10f);
            SetSurfaceEdgeFadeValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeFadeCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceEdgeFadePercent.Update(m_ActiveSurfaceEdgeFadeCurrent);
            m_SurfaceEdgeFadesJson.Update(SerializeSurfaceEdgeFadeValues());
        }

        private void SetSurfaceEdgeFadeEndPreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float percent)) return;
            m_ActiveSurfaceEdgeFadeEndCurrent = Mathf.Clamp(percent, 0f, 100f);
            SetSurfaceEdgeFadeEndValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeFadeEndCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceEdgeFadeEndPercent.Update(m_ActiveSurfaceEdgeFadeEndCurrent);
            m_SurfaceEdgeFadeEndsJson.Update(SerializeSurfaceEdgeFadeEndValues());
        }

        private void SetSurfaceEdgeNoisePreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float percent)) return;
            Mod.Log.InfoFormat("Surface slider input: kind='Edge noise', raw='{0}', parsed={1}, maximum={2}", value, percent, 50f);
            m_ActiveSurfaceEdgeNoiseCurrent = Mathf.Clamp(percent, 0f, 10f);
            SetSurfaceEdgeNoiseValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeNoiseCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceEdgeNoisePercent.Update(m_ActiveSurfaceEdgeNoiseCurrent);
            m_SurfaceEdgeNoisesJson.Update(SerializeSurfaceEdgeNoiseValues());
        }

        private void SetSurfaceEdgeNoiseScalePreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale)) return;
            m_ActiveSurfaceEdgeNoiseScaleCurrent = Mathf.Clamp(scale, 0.01f, 10f);
            SetSurfaceEdgeNoiseScaleValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeNoiseScaleCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceEdgeNoiseScale.Update(m_ActiveSurfaceEdgeNoiseScaleCurrent);
            m_SurfaceEdgeNoiseScalesJson.Update(SerializeSurfaceEdgeNoiseScaleValues());
        }

        private void SetSurfaceBaseColorPreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId)) return;
            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length != 4 ||
                !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hue) ||
                !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float saturation) ||
                !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float brightness) ||
                !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float alpha)) return;
            m_ActiveSurfaceBaseColorCurrent = Color.HSVToRGB(Mathf.Repeat(hue, 1f), Mathf.Clamp01(saturation), Mathf.Clamp01(brightness));
            m_ActiveSurfaceBaseColorCurrent.a = Mathf.Clamp01(alpha);
            m_SurfaceBaseColorHue.Update(Mathf.Repeat(hue, 1f));
            m_SurfaceBaseColorSaturation.Update(Mathf.Clamp01(saturation));
            m_SurfaceBaseColorBrightness.Update(Mathf.Clamp01(brightness));
            m_SurfaceBaseColorAlpha.Update(Mathf.Clamp01(alpha));
            if (m_SurfaceBaseColorDefaults.TryGetValue(m_ActiveSurfaceScaleId, out Color defaultBaseColor) &&
                ColorDifference(m_ActiveSurfaceBaseColorCurrent, defaultBaseColor) < 0.0001f)
                m_SurfaceBaseColors.Remove(m_ActiveSurfaceScaleId);
            else
                m_SurfaceBaseColors[m_ActiveSurfaceScaleId] = m_ActiveSurfaceBaseColorCurrent;
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceBaseColorsJson.Update(SerializeSurfaceBaseColors());
        }

        private void ApplySurfaceScale()
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId))
            {
                m_SurfaceScaleEditorOpen.Update(false);
                return;
            }

            if (Mathf.Abs(m_ActiveSurfaceScaleCurrent - m_ActiveSurfaceScaleDefault) < 0.0001f)
                m_SurfaceScaleValues.Remove(m_ActiveSurfaceScaleId);
            else
                SetSurfaceScaleValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent);
            SetSurfaceEdgeFadeValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeFadeCurrent);
            // Edge fade zero means the complete prefab default pair, so the
            // hidden end value must not be persisted independently.
            if (Mathf.Abs(m_ActiveSurfaceEdgeFadeCurrent) < 0.0001f)
                m_SurfaceEdgeFadeEndValues.Remove(m_ActiveSurfaceScaleId);
            else
                SetSurfaceEdgeFadeEndValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeFadeEndCurrent);
            if (Mathf.Abs(m_ActiveSurfaceEdgeNoiseCurrent - m_ActiveSurfaceEdgeNoiseDefault) < 0.0001f)
                m_SurfaceEdgeNoiseValues.Remove(m_ActiveSurfaceScaleId);
            else
                SetSurfaceEdgeNoiseValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeNoiseCurrent);
            if (Mathf.Abs(m_ActiveSurfaceEdgeNoiseScaleCurrent - m_ActiveSurfaceEdgeNoiseScaleDefault) < 0.0001f)
                m_SurfaceEdgeNoiseScaleValues.Remove(m_ActiveSurfaceScaleId);
            else
                SetSurfaceEdgeNoiseScaleValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeNoiseScaleCurrent);
            if (ColorDifference(m_ActiveSurfaceBaseColorCurrent, m_ActiveSurfaceBaseColorDefault) < 0.0001f)
                m_SurfaceBaseColors.Remove(m_ActiveSurfaceScaleId);
            else
                m_SurfaceBaseColors[m_ActiveSurfaceScaleId] = m_ActiveSurfaceBaseColorCurrent;
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfaceScalesJson = SerializeSurfaceScaleValues();
                Mod.Settings.SurfaceEdgeFadesJson = SerializeSurfaceEdgeFadeValues();
                Mod.Settings.SurfaceEdgeFadeEndsJson = SerializeSurfaceEdgeFadeEndValues();
                Mod.Settings.SurfaceEdgeNoisesJson = SerializeSurfaceEdgeNoiseValues();
                Mod.Settings.SurfaceEdgeNoiseScalesJson = SerializeSurfaceEdgeNoiseScaleValues();
                Mod.Settings.SurfaceBaseColorsJson = SerializeSurfaceBaseColors();
                Mod.Settings.ApplyAndSave();
            }

            m_SurfaceScalesJson.Update(SerializeSurfaceScaleValues());
            m_SurfaceEdgeFadesJson.Update(SerializeSurfaceEdgeFadeValues());
            m_SurfaceEdgeFadeEndsJson.Update(SerializeSurfaceEdgeFadeEndValues());
            m_SurfaceEdgeNoisesJson.Update(SerializeSurfaceEdgeNoiseValues());
            m_SurfaceEdgeNoiseScalesJson.Update(SerializeSurfaceEdgeNoiseScaleValues());
            m_SurfaceBaseColorsJson.Update(SerializeSurfaceBaseColors());
            CloseSurfaceScaleEditor();
        }

        private void RevertSurfaceScale()
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId)) return;
            m_SurfaceScaleValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceEdgeFadeValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceEdgeFadeEndValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceEdgeNoiseValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceEdgeNoiseScaleValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceBaseColors.Remove(m_ActiveSurfaceScaleId);
            m_ActiveSurfaceScaleCurrent = m_ActiveSurfaceScaleDefault;
            m_ActiveSurfaceEdgeFadeCurrent = 0;
            m_ActiveSurfaceEdgeNoiseCurrent = m_ActiveSurfaceEdgeNoiseDefault;
            m_ActiveSurfaceEdgeNoiseScaleCurrent = m_ActiveSurfaceEdgeNoiseScaleDefault;
            m_ActiveSurfaceBaseColorCurrent = m_ActiveSurfaceBaseColorDefault;
            Color.RGBToHSV(m_ActiveSurfaceBaseColorCurrent, out float revertHue, out float revertSaturation, out float revertValue);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceScaleHundredths.Update(Mathf.RoundToInt(m_ActiveSurfaceScaleCurrent * 100f));
            m_SurfaceEdgeFadePercent.Update(0);
            m_SurfaceEdgeFadeEndPercent.Update(0);
            m_SurfaceEdgeNoisePercent.Update(m_ActiveSurfaceEdgeNoiseCurrent);
            m_SurfaceEdgeNoiseScale.Update(m_ActiveSurfaceEdgeNoiseScaleCurrent);
            m_SurfaceBaseColorHue.Update(revertHue);
            m_SurfaceBaseColorSaturation.Update(revertSaturation);
            m_SurfaceBaseColorBrightness.Update(revertValue);
            m_SurfaceBaseColorAlpha.Update(m_ActiveSurfaceBaseColorCurrent.a);
            m_SurfaceScaleRevertVersion.Update(++m_SurfaceScaleRevertCounter);
            m_SurfaceScalesJson.Update(SerializeSurfaceScaleValues());
            m_SurfaceEdgeFadesJson.Update(SerializeSurfaceEdgeFadeValues());
            m_SurfaceEdgeNoisesJson.Update(SerializeSurfaceEdgeNoiseValues());
            m_SurfaceEdgeNoiseScalesJson.Update(SerializeSurfaceEdgeNoiseScaleValues());
            m_SurfaceBaseColorsJson.Update(SerializeSurfaceBaseColors());
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfaceScalesJson = SerializeSurfaceScaleValues();
                Mod.Settings.SurfaceEdgeFadesJson = SerializeSurfaceEdgeFadeValues();
                Mod.Settings.SurfaceEdgeFadeEndsJson = SerializeSurfaceEdgeFadeEndValues();
                Mod.Settings.SurfaceEdgeNoisesJson = SerializeSurfaceEdgeNoiseValues();
                Mod.Settings.SurfaceEdgeNoiseScalesJson = SerializeSurfaceEdgeNoiseScaleValues();
                Mod.Settings.SurfaceBaseColorsJson = SerializeSurfaceBaseColors();
                Mod.Settings.ApplyAndSave();
            }
        }

        private void CancelSurfaceScale()
        {
            if (!string.IsNullOrEmpty(m_ActiveSurfaceScaleId))
            {
                if (m_ActiveSurfaceScaleHadSavedValue)
                {
                    m_SurfaceScaleValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceScaleSavedValue;
                }
                else
                {
                    m_SurfaceScaleValues.Remove(m_ActiveSurfaceScaleId);
                }

                if (m_ActiveSurfaceEdgeFadeHadSavedValue)
                {
                    m_SurfaceEdgeFadeValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceEdgeFadeSavedValue;
                }
                else
                {
                    m_SurfaceEdgeFadeValues.Remove(m_ActiveSurfaceScaleId);
                }

                if (m_ActiveSurfaceEdgeFadeEndHadSavedValue)
                    m_SurfaceEdgeFadeEndValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceEdgeFadeEndSavedValue;
                else
                    m_SurfaceEdgeFadeEndValues.Remove(m_ActiveSurfaceScaleId);

                if (m_ActiveSurfaceEdgeNoiseHadSavedValue)
                    m_SurfaceEdgeNoiseValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceEdgeNoiseSavedValue;
                else
                    m_SurfaceEdgeNoiseValues.Remove(m_ActiveSurfaceScaleId);

                if (m_ActiveSurfaceEdgeNoiseScaleHadSavedValue)
                    m_SurfaceEdgeNoiseScaleValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceEdgeNoiseScaleSavedValue;
                else
                    m_SurfaceEdgeNoiseScaleValues.Remove(m_ActiveSurfaceScaleId);

                if (m_ActiveSurfaceBaseColorHadSavedValue)
                    m_SurfaceBaseColors[m_ActiveSurfaceScaleId] = m_ActiveSurfaceBaseColorSavedValue;
                else
                    m_SurfaceBaseColors.Remove(m_ActiveSurfaceScaleId);

                ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleBefore, updatePrefab: true);
                m_SurfaceScalesJson.Update(SerializeSurfaceScaleValues());
                m_SurfaceEdgeFadesJson.Update(SerializeSurfaceEdgeFadeValues());
                m_SurfaceEdgeFadeEndsJson.Update(SerializeSurfaceEdgeFadeEndValues());
                m_SurfaceEdgeNoisesJson.Update(SerializeSurfaceEdgeNoiseValues());
                m_SurfaceEdgeNoiseScalesJson.Update(SerializeSurfaceEdgeNoiseScaleValues());
                m_SurfaceBaseColorsJson.Update(SerializeSurfaceBaseColors());
            }

            CloseSurfaceScaleEditor();
        }

        private void CloseSurfaceScaleEditor()
        {
            m_ActiveSurfaceScaleId = null;
            m_SurfaceScaleEditorIsOpen = false;
            m_SurfaceScaleSurfaceId.Update("");
            m_SurfaceScaleSurfaceName.Update("");
            m_SurfaceEdgeFadePercent.Update(0);
            m_SurfaceEdgeFadeEndPercent.Update(0);
            m_SurfaceEdgeNoisePercent.Update(0);
            m_SurfaceEdgeNoiseScale.Update(0.2f);
            m_SurfaceScaleEditorOpen.Update(false);
        }

        private void SetSurfaceScaleValue(string surfaceId, float scale)
        {
            scale = ClampSurfaceScale(scale);
            if (Mathf.Abs(scale - 1f) < 0.001f)
            {
                m_SurfaceScaleValues.Remove(surfaceId);
            }
            else
            {
                m_SurfaceScaleValues[surfaceId] = scale;
            }
        }

        private static float ColorDifference(Color first, Color second)
        {
            return Mathf.Max(Mathf.Abs(first.r - second.r), Mathf.Abs(first.g - second.g), Mathf.Abs(first.b - second.b), Mathf.Abs(first.a - second.a));
        }

        private void SetSurfaceEdgeFadeValue(string surfaceId, float percent)
        {
            percent = Mathf.Clamp(percent, 0f, 10f);
            if (Mathf.Abs(percent) < 0.0001f)
            {
                m_SurfaceEdgeFadeValues.Remove(surfaceId);
            }
            else
            {
                m_SurfaceEdgeFadeValues[surfaceId] = percent;
            }
        }

        private void SetSurfaceEdgeFadeEndValue(string surfaceId, float percent)
        {
            percent = Mathf.Clamp(percent, 0f, 100f);
            if (Mathf.Abs(percent) < 0.0001f) m_SurfaceEdgeFadeEndValues.Remove(surfaceId);
            else m_SurfaceEdgeFadeEndValues[surfaceId] = percent;
        }

        private void SetSurfaceEdgeNoiseValue(string surfaceId, float percent)
        {
            percent = Mathf.Clamp(percent, 0f, 10f);
            if (Mathf.Abs(percent) < 0.0001f) m_SurfaceEdgeNoiseValues.Remove(surfaceId);
            else m_SurfaceEdgeNoiseValues[surfaceId] = percent;
        }

        private void SetSurfaceEdgeNoiseScaleValue(string surfaceId, float value)
        {
            m_SurfaceEdgeNoiseScaleValues[surfaceId] = Mathf.Clamp(value, 0.01f, 10f);
        }


        private bool TryFindSurfacePrefab(string surfaceId, out Entity surfaceEntity, out PrefabBase surfacePrefab, out RenderedArea renderedArea)
        {
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if ((!EntityManager.HasComponent<SurfaceData>(entity) && !EntityManager.HasComponent<AreaData>(entity)) ||
                        !m_PrefabSystemForScale().TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab prefab) ||
                        prefab == null || !string.Equals(prefab.GetPrefabID().ToString(), surfaceId, StringComparison.Ordinal) ||
                        !prefab.TryGet<RenderedArea>(out renderedArea) || renderedArea == null)
                    {
                        continue;
                    }

                    surfaceEntity = entity;
                    surfacePrefab = prefab;
                    return true;
                }
            }

            surfaceEntity = Entity.Null;
            surfacePrefab = null;
            renderedArea = null;
            return false;
        }

        private PrefabSystem m_PrefabSystemForScale()
        {
            return World.GetExistingSystemManaged<PrefabSystem>();
        }

        private void RestoreConfiguredSurfaceScales(PrefabSystem prefabSystem)
        {
            m_AreaBatchSystem ??= World.GetOrCreateSystemManaged<AreaBatchSystem>();
            HashSet<string> configuredSurfaceIds = new HashSet<string>(m_SurfaceScaleValues.Keys, StringComparer.Ordinal);
            configuredSurfaceIds.UnionWith(m_SurfaceEdgeFadeValues.Keys);
            configuredSurfaceIds.UnionWith(m_SurfaceEdgeFadeEndValues.Keys);
            configuredSurfaceIds.UnionWith(m_SurfaceEdgeNoiseValues.Keys);
            configuredSurfaceIds.UnionWith(m_SurfaceEdgeNoiseScaleValues.Keys);
            configuredSurfaceIds.UnionWith(m_SurfaceBaseColors.Keys);
            foreach (string surfaceId in configuredSurfaceIds)
            {
                if (m_RestoredSurfaceScaleIds.Contains(surfaceId))
                {
                    continue;
                }

                float scale = m_SurfaceScaleValues.TryGetValue(surfaceId, out float savedScale)
                    ? savedScale
                    : (m_SurfaceScaleBaseValues.TryGetValue(surfaceId, out float baseUvScale) && Mathf.Abs(baseUvScale) > 0.0001f
                        ? ClampSurfaceScale(1f / baseUvScale)
                        : 1f);
                if (TryFindSurfacePrefab(surfaceId, out _, out PrefabBase _, out RenderedArea renderedArea))
                {
                    if (ApplySurfaceScaleToPrefab(surfaceId, scale, updatePrefab: true, prefabSystem))
                    {
                        m_RestoredSurfaceScaleIds.Add(surfaceId);
                    }
                }
            }
        }

        // Capture the unmodified prefab appearance before a map can instantiate
        // surfaces or restore previously saved runtime changes.
        private void CaptureSurfaceDefaults(PrefabSystem prefabSystem)
        {
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab prefab) || prefab == null ||
                        !prefab.TryGet<RenderedArea>(out RenderedArea renderedArea) || renderedArea == null)
                    {
                        continue;
                    }

                    string surfaceId = prefab.GetPrefabID().ToString();
                    Material material = renderedArea.m_Material;
                    float2 edgeFade = renderedArea.m_EdgeFadeRange;
                    float2 edgeNoise = renderedArea.m_EdgeNoise;
                    Color baseColor = renderedArea.m_BaseColor;
                    if (material != null && material.HasProperty(s_EdgeFadeRangeProperty))
                    {
                        Vector4 value = material.GetVector(s_EdgeFadeRangeProperty);
                        edgeFade = new float2(value.x, value.y);
                    }
                    if (material != null && material.HasProperty(s_EdgeNoiseProperty))
                    {
                        Vector4 value = material.GetVector(s_EdgeNoiseProperty);
                        edgeNoise = new float2(value.x, value.y);
                    }
                    if (material != null && material.HasProperty(s_BaseColorProperty)) baseColor = material.GetColor(s_BaseColorProperty);

                    if (!m_SurfaceScaleBaseValues.ContainsKey(surfaceId)) m_SurfaceScaleBaseValues[surfaceId] = renderedArea.m_UVScale;
                    if (!m_SurfaceEdgeFadeBaseValues.ContainsKey(surfaceId)) m_SurfaceEdgeFadeBaseValues[surfaceId] = edgeFade;
                    if (!m_SurfaceEdgeNoiseBaseValues.ContainsKey(surfaceId)) m_SurfaceEdgeNoiseBaseValues[surfaceId] = edgeNoise;
                    if (!m_SurfaceBaseColorDefaults.ContainsKey(surfaceId)) m_SurfaceBaseColorDefaults[surfaceId] = baseColor;
                }
            }
        }

        private bool ApplySurfaceScaleToPrefab(string surfaceId, float scale, bool updatePrefab, PrefabSystem prefabSystem = null)
        {
            prefabSystem ??= World.GetExistingSystemManaged<PrefabSystem>();
            if (prefabSystem == null)
            {
                return false;
            }

            m_AreaBatchSystem ??= World.GetOrCreateSystemManaged<AreaBatchSystem>();
            bool applied = false;
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if ((!EntityManager.HasComponent<SurfaceData>(entity) && !EntityManager.HasComponent<AreaData>(entity)) ||
                        !prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab prefab) || prefab == null ||
                        !string.Equals(prefab.GetPrefabID().ToString(), surfaceId, StringComparison.Ordinal) ||
                        !prefab.TryGet<RenderedArea>(out RenderedArea renderedArea) || renderedArea == null)
                    {
                        continue;
                    }

                    int prefabBatchIndex = EntityManager.HasComponent<RenderedAreaData>(entity)
                        ? EntityManager.GetComponentData<RenderedAreaData>(entity).m_BatchIndex
                        : -1;
                    Material sourceMaterial = renderedArea.m_Material;
                    int materialUvScaleProperty = s_AreaBatchUvScaleField != null
                        ? (int)s_AreaBatchUvScaleField.GetValue(m_AreaBatchSystem)
                        : 0;
                    bool sourceHasScale = sourceMaterial != null && materialUvScaleProperty != 0 &&
                        sourceMaterial.HasProperty(materialUvScaleProperty);
                    if (!sourceHasScale && prefabBatchIndex >= 0 &&
                        m_AreaBatchSystem.GetAreaBatch(prefabBatchIndex, out _, out _, out _, out Material batchMaterial, out _, out _, out _) &&
                        batchMaterial != null && materialUvScaleProperty != 0 && batchMaterial.HasProperty(materialUvScaleProperty))
                    {
                        sourceMaterial = batchMaterial;
                        sourceHasScale = true;
                    }

                    // A prefab's batch may not exist yet when settings are restored
                    // just after map load. Try a live instance batch and retry on
                    // later updates if neither material is ready.
                    if (!sourceHasScale)
                    {
                        using (NativeArray<Entity> instances = m_SurfaceScaleInstancesQuery.ToEntityArray(Allocator.Temp))
                        {
                            foreach (Entity instance in instances)
                            {
                                if (!EntityManager.Exists(instance) ||
                                    EntityManager.GetComponentData<PrefabRef>(instance).m_Prefab != entity)
                                {
                                    continue;
                                }

                                int instanceBatch = EntityManager.GetComponentData<Batch>(instance).m_BatchIndex;
                                if (m_AreaBatchSystem.GetAreaBatch(instanceBatch, out _, out _, out _, out Material instanceMaterial, out _, out _, out _) &&
                                    instanceMaterial != null && materialUvScaleProperty != 0 && instanceMaterial.HasProperty(materialUvScaleProperty))
                                {
                                    sourceMaterial = instanceMaterial;
                                    sourceHasScale = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!sourceHasScale || sourceMaterial == null)
                    {
                        if (m_SurfaceScaleMaterialUnavailableLogged.Add(surfaceId))
                        {
                            Mod.Log.InfoFormat(
                                "Surface texture scale is waiting for its AreaBatch material: surface='{0}', prefabBatch={1}",
                                prefab.name,
                                prefabBatchIndex);
                        }
                        continue;
                    }

                    if (!m_SurfaceScaleBaseValues.TryGetValue(surfaceId, out float baseScale))
                    {
                        baseScale = sourceMaterial.GetFloat(materialUvScaleProperty);
                        m_SurfaceScaleBaseValues[surfaceId] = baseScale;
                    }

                    if (!m_SurfaceEdgeFadeBaseValues.ContainsKey(surfaceId))
                    {
                        float2 baseEdgeFadeRange = renderedArea.m_EdgeFadeRange;
                        if (sourceMaterial.HasProperty(s_EdgeFadeRangeProperty))
                        {
                            Vector4 value = sourceMaterial.GetVector(s_EdgeFadeRangeProperty);
                            baseEdgeFadeRange = new float2(value.x, value.y);
                        }

                        m_SurfaceEdgeFadeBaseValues[surfaceId] = baseEdgeFadeRange;
                    }

                    if (!m_SurfaceEdgeNoiseBaseValues.ContainsKey(surfaceId))
                    {
                        float2 baseEdgeNoise = renderedArea.m_EdgeNoise;
                        if (sourceMaterial.HasProperty(s_EdgeNoiseProperty))
                        {
                            Vector4 value = sourceMaterial.GetVector(s_EdgeNoiseProperty);
                            baseEdgeNoise = new float2(value.x, value.y);
                        }
                        m_SurfaceEdgeNoiseBaseValues[surfaceId] = baseEdgeNoise;
                    }

                    // AreaBatchSystem applies RenderedArea.m_UVScale only when the
                    // component uses OverrideMaterial. Keep a private material copy
                    // and copy its scalar settings into RenderedArea before enabling
                    // that native path, so this does not mutate shared materials or
                    // reset the source surface's appearance.
                    if (!m_SurfaceScaleMaterialCloneIds.Contains(surfaceId) && sourceMaterial != null)
                    {
                        Material prefabMaterial = new Material(sourceMaterial)
                        {
                            name = "Replacement Solutions Surface Material (" + prefab.name + ")",
                        };
                        CopyMaterialSettingsToRenderedArea(prefabMaterial, renderedArea);
                        renderedArea.m_Material = prefabMaterial;
                        renderedArea.m_Version = RenderedArea.Version.OverrideMaterial;
                        m_SurfaceScaleMaterialCloneIds.Add(surfaceId);
                        Mod.Log.InfoFormat(
                            "Surface texture scale material prepared: surface='{0}', sourceMaterial='{1}', baseline={2}, shader='{3}', version={4}",
                            prefab.name,
                            sourceMaterial.name,
                            baseScale,
                            sourceMaterial.shader != null ? sourceMaterial.shader.name : "<no shader>",
                            renderedArea.m_Version);
                    }

                    // The editor exposes an intuitive texture-size factor. The game
                    // stores the reciprocal UV scale, so apply exactly 1 / Scale.
                    float userScale = ClampSurfaceScale(scale);
                    float scaledUv = 1f / userScale;
                    renderedArea.m_UVScale = scaledUv;
                    renderedArea.m_Material.SetFloat(materialUvScaleProperty, scaledUv);
                    float edgeFadePercent = m_SurfaceEdgeFadeValues.TryGetValue(surfaceId, out float savedEdgeFade)
                        ? savedEdgeFade
                        : m_SurfaceEdgeFadeBaseValues[surfaceId].x;
                    float edgeFadeEndPercent = m_SurfaceEdgeFadeEndValues.TryGetValue(surfaceId, out float savedEdgeFadeEnd)
                        ? savedEdgeFadeEnd
                        : m_SurfaceEdgeFadeBaseValues[surfaceId].y;
                    float2 edgeFadeRange = ExpandSurfaceEdgeFadeRange(
                        m_SurfaceEdgeFadeBaseValues[surfaceId],
                        edgeFadePercent,
                        edgeFadeEndPercent);
                    renderedArea.m_EdgeFadeRange = edgeFadeRange;
                    if (s_EdgeFadeRangeProperty != 0 && renderedArea.m_Material.HasProperty(s_EdgeFadeRangeProperty))
                    {
                        renderedArea.m_Material.SetVector(
                            s_EdgeFadeRangeProperty,
                            new Vector4(edgeFadeRange.x, edgeFadeRange.y, 0f, 0f));
                    }
                    float edgeNoisePercent = m_SurfaceEdgeNoiseValues.TryGetValue(surfaceId, out float savedEdgeNoise)
                        ? savedEdgeNoise
                        : m_SurfaceEdgeNoiseBaseValues[surfaceId].x;
                    float2 edgeNoise = m_SurfaceEdgeNoiseBaseValues[surfaceId];
                    // The shader's first EdgeNoise component is noise strength; the
                    // second is its scale. Leave the source scale intact and expose
                    // strength as a percentage so zero restores the exact source.
                    // Keep the exact user input in the material/batch. The UI
                    // range is intentionally not normalized here.
                    edgeNoise.x = edgeNoisePercent;
                    if (m_SurfaceEdgeNoiseScaleValues.TryGetValue(surfaceId, out float savedEdgeNoiseScale))
                        edgeNoise.y = Mathf.Max(0.01f, savedEdgeNoiseScale);
                    Mod.Log.InfoFormat(
                        "Surface appearance output: surface='{0}', scale={1}, uvScale={2}, edgeFadeInput={3}, edgeFadeEndInput={4}, edgeFadeRange=({5},{6}), edgeNoiseInput={7}, edgeNoise=({8},{9})",
                        prefab.name,
                        userScale,
                        scaledUv,
                        edgeFadePercent,
                        edgeFadeEndPercent,
                        edgeFadeRange.x,
                        edgeFadeRange.y,
                        edgeNoisePercent,
                        edgeNoise.x,
                        edgeNoise.y);
                    renderedArea.m_EdgeNoise = edgeNoise;
                    if (s_EdgeNoiseProperty != 0 && renderedArea.m_Material.HasProperty(s_EdgeNoiseProperty))
                    {
                        renderedArea.m_Material.SetVector(s_EdgeNoiseProperty, new Vector4(edgeNoise.x, edgeNoise.y, 0f, 0f));
                    }
                    Color baseColor = m_SurfaceBaseColors.TryGetValue(surfaceId, out Color savedBaseColor)
                        ? savedBaseColor
                        : (m_SurfaceBaseColorDefaults.TryGetValue(surfaceId, out Color defaultBaseColor) ? defaultBaseColor : Color.white);
                    renderedArea.m_BaseColor = baseColor;
                    if (renderedArea.m_Material.HasProperty(s_BaseColorProperty)) renderedArea.m_Material.SetColor(s_BaseColorProperty, baseColor);
                    if (updatePrefab)
                    {
                        prefabSystem.UpdatePrefab(prefab);
                    }

                    SetAreaBatchUvScale(prefabBatchIndex, scaledUv);
                    SetAreaBatchEdgeFadeRange(prefabBatchIndex, edgeFadeRange);
                    SetAreaBatchEdgeNoise(prefabBatchIndex, edgeNoise);
                    SetAreaBatchBaseColor(prefabBatchIndex, baseColor);

                    // Existing area instances can hold their own batch index even
                    // though they share this surface prefab. Update those batches
                    // too, otherwise only newly created/replaced surfaces use the
                    // selected UV scale.
                    using (NativeArray<Entity> instances = m_SurfaceScaleInstancesQuery.ToEntityArray(Allocator.Temp))
                    {
                        foreach (Entity instance in instances)
                        {
                            if (!EntityManager.Exists(instance) ||
                                EntityManager.GetComponentData<PrefabRef>(instance).m_Prefab != entity)
                            {
                                continue;
                            }

                            int instanceBatch = EntityManager.GetComponentData<Batch>(instance).m_BatchIndex;
                            SetAreaBatchUvScale(instanceBatch, scaledUv);
                            SetAreaBatchEdgeFadeRange(instanceBatch, edgeFadeRange);
                            SetAreaBatchEdgeNoise(instanceBatch, edgeNoise);
                            SetAreaBatchBaseColor(instanceBatch, baseColor);
                        }
                    }

                    // AreaBatchSystem may keep the material in its enabled-shader
                    // cache after its properties were changed. Notify it once so
                    // the new edge fade reaches the visible batches immediately.
                    m_AreaBatchSystem.EnabledShadersUpdated();

                    Mod.Log.InfoFormat(
                        "Surface appearance applied: surface='{0}', scale={1}, uvScale={2}, edgeFadePercent={3}, edgeFadeRange=({4},{5}), edgeNoisePercent={6}, edgeNoise=({7},{8}), prefabBatch={9}, updatedPrefab={10}",
                        prefab.name,
                        userScale,
                        scaledUv,
                        edgeFadePercent,
                        edgeFadeRange.x,
                        edgeFadeRange.y,
                        edgeNoisePercent,
                        edgeNoise.x,
                        edgeNoise.y,
                        prefabBatchIndex,
                        updatePrefab);
                    applied = true;
                }
            }

            if (applied && (m_SurfaceScaleValues.ContainsKey(surfaceId) || m_SurfaceEdgeFadeValues.ContainsKey(surfaceId) || m_SurfaceEdgeNoiseValues.ContainsKey(surfaceId) || m_SurfaceEdgeNoiseScaleValues.ContainsKey(surfaceId) || m_SurfaceBaseColors.ContainsKey(surfaceId)))
            {
                m_RestoredSurfaceScaleIds.Add(surfaceId);
            }
            else
            {
                m_RestoredSurfaceScaleIds.Remove(surfaceId);
            }
            return applied;
        }

        private static float2 ExpandSurfaceEdgeFadeRange(float2 baseRange, float startPercent, float endPercent)
        {
            return new float2(
                startPercent <= 0
                    ? baseRange.x
                    : startPercent,
                startPercent <= 0
                    ? baseRange.y
                    : 0.001f);
        }


        private static void CopyMaterialSettingsToRenderedArea(Material material, RenderedArea renderedArea)
        {
            if (material.HasProperty(s_BaseColorProperty)) renderedArea.m_BaseColor = material.GetColor(s_BaseColorProperty);
            if (material.HasProperty(s_MetallicProperty)) renderedArea.m_Metallic = material.GetFloat(s_MetallicProperty);
            if (material.HasProperty(s_SmoothnessProperty)) renderedArea.m_Smoothness = material.GetFloat(s_SmoothnessProperty);
            if (material.HasProperty(s_NormalOpacityProperty)) renderedArea.m_NormalOpacity = material.GetFloat(s_NormalOpacityProperty);
            if (material.HasProperty(s_MetallicOpacityProperty)) renderedArea.m_MetallicOpacity = material.GetFloat(s_MetallicOpacityProperty);
            if (material.HasProperty(s_NormalAlphaSourceProperty)) renderedArea.m_NormalAlphaSource = material.GetFloat(s_NormalAlphaSourceProperty);
            if (material.HasProperty(s_MetallicAlphaSourceProperty)) renderedArea.m_MetallicAlphaSource = material.GetFloat(s_MetallicAlphaSourceProperty);
            if (material.HasProperty(s_EdgeNormalProperty)) renderedArea.m_EdgeNormal = material.GetFloat(s_EdgeNormalProperty);
            if (material.HasProperty(s_EdgeFadeRangeProperty))
            {
                Vector4 value = material.GetVector(s_EdgeFadeRangeProperty);
                renderedArea.m_EdgeFadeRange = new float2(value.x, value.y);
            }
            if (material.HasProperty(s_EdgeNormalRangeProperty))
            {
                Vector4 value = material.GetVector(s_EdgeNormalRangeProperty);
                renderedArea.m_EdgeNormalRange = new float2(value.x, value.y);
            }
            if (material.HasProperty(s_EdgeNoiseProperty))
            {
                Vector4 value = material.GetVector(s_EdgeNoiseProperty);
                renderedArea.m_EdgeNoise = new float2(value.x, value.y);
            }
        }

        private void SetAreaBatchUvScale(int batchIndex, float uvScale)
        {
            if (m_AreaBatchSystem == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                s_AreaBatchUvScaleField == null)
            {
                return;
            }

            int uvScaleProperty = (int)s_AreaBatchUvScaleField.GetValue(m_AreaBatchSystem);
            if (uvScaleProperty == 0 ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) ||
                material == null || !material.HasProperty(uvScaleProperty))
            {
                return;
            }

            material.SetFloat(uvScaleProperty, uvScale);
        }

        private void SetAreaBatchEdgeFadeRange(int batchIndex, float2 edgeFadeRange)
        {
            if (m_AreaBatchSystem == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                s_EdgeFadeRangeProperty == 0 ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) ||
                material == null || !material.HasProperty(s_EdgeFadeRangeProperty))
            {
                return;
            }

            material.SetVector(
                s_EdgeFadeRangeProperty,
                new Vector4(edgeFadeRange.x, edgeFadeRange.y, 0f, 0f));
        }

        private void SetAreaBatchEdgeNoise(int batchIndex, float2 edgeNoise)
        {
            if (m_AreaBatchSystem == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                s_EdgeNoiseProperty == 0 ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) ||
                material == null || !material.HasProperty(s_EdgeNoiseProperty))
            {
                return;
            }

            material.SetVector(s_EdgeNoiseProperty, new Vector4(edgeNoise.x, edgeNoise.y, 0f, 0f));
        }

        private void SetAreaBatchBaseColor(int batchIndex, Color color)
        {
            if (m_AreaBatchSystem == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                s_BaseColorProperty == 0 ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) ||
                material == null || !material.HasProperty(s_BaseColorProperty)) return;
            material.SetColor(s_BaseColorProperty, color);
        }



    }
}

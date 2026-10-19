using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Fila de cuadrados que muestra la secuencia que el jugador va haciendo.
///   - Vacío / pendiente = negro con "?"
///   - Gesto correcto    = verde (con el nombre o el ícono del gesto realizado)
///   - Secuencia errónea = todos rojos (BattleManager llama a ResetSlots() poco después)
/// Se crea por código: pon este script en un objeto vacío HIJO DEL CANVAS (ej. "PanelSecuencia").
///
/// Íconos opcionales (PNG blanco con fondo transparente): se tiñen de negro / verde / rojo.
/// </summary>
public class SequenceDisplay : MonoBehaviour
{
    [Serializable]
    public class GestureIcon
    {
        [Tooltip("PALMA, REPOSO (= puño), DERECHA, IZQUIERDA, DOS_DEDOS o UN_DEDO")]
        public string gesture;
        public Sprite sprite;
    }

    private class Slot
    {
        public Image image;
        public TMP_Text label;
    }

    [Header("Tamaño")]
    [SerializeField] private float slotSize = 100f;
    [SerializeField] private float spacing = 24f;

    [Header("Colores")]
    [SerializeField] private Color pendingColor = Color.black;
    [SerializeField] private Color doneColor = new Color(0.20f, 0.78f, 0.25f);
    [SerializeField] private Color errorColor = new Color(0.85f, 0.15f, 0.15f);
    [SerializeField] private Color labelColor = Color.white;
    [SerializeField] private string emptyLabel = "?";

    [Header("Íconos (opcional). Sin sprite se muestra un cuadrado con el nombre")]
    [SerializeField] private GestureIcon[] icons;

    private readonly List<Slot> slots = new List<Slot>();

    /// <summary>Crea "count" cuadrados vacíos (negros).</summary>
    public void ShowEmpty(int count)
    {
        Clear();
        if (count <= 0) return;

        float total = count * slotSize + (count - 1) * spacing;

        for (int i = 0; i < count; i++)
        {
            var go = new GameObject($"Slot_{i}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(slotSize, slotSize);
            rt.anchoredPosition = new Vector2(-total / 2f + slotSize / 2f + i * (slotSize + spacing), 0f);

            var img = go.GetComponent<Image>();
            img.color = pendingColor;
            img.raycastTarget = false;

            slots.Add(new Slot { image = img, label = CreateLabel(go.transform, emptyLabel) });
        }
    }

    /// <summary>Rellena el cuadrado "index" con el gesto realizado y lo pone en verde.</summary>
    public void SetSlot(int index, string gesture, string labelText)
    {
        if (index < 0 || index >= slots.Count) return;
        Slot s = slots[index];
        if (s.image == null) return;

        Sprite sprite = FindSprite(gesture);
        if (sprite != null)
        {
            s.image.sprite = sprite;
            s.image.preserveAspect = true;
            s.label.gameObject.SetActive(false);
        }
        else
        {
            s.image.sprite = null;
            s.label.text = labelText;
            s.label.gameObject.SetActive(true);
        }
        s.image.color = doneColor;
    }

    /// <summary>Pone TODOS los cuadrados en rojo (secuencia equivocada).</summary>
    public void ShowError()
    {
        foreach (Slot s in slots)
            if (s.image != null) s.image.color = errorColor;
    }

    /// <summary>Vuelve todos los cuadrados a negro y vacíos.</summary>
    public void ResetSlots()
    {
        foreach (Slot s in slots)
        {
            if (s.image == null) continue;
            s.image.sprite = null;
            s.image.color = pendingColor;
            s.label.text = emptyLabel;
            s.label.gameObject.SetActive(true);
        }
    }

    /// <summary>Quita la secuencia de la pantalla.</summary>
    public void Hide() => Clear();

    private void Clear()
    {
        foreach (Slot s in slots)
            if (s.image != null) Destroy(s.image.gameObject);
        slots.Clear();
    }

    private Sprite FindSprite(string gesture)
    {
        if (icons == null) return null;
        foreach (GestureIcon icon in icons)
            if (icon != null && icon.sprite != null && icon.gesture == gesture)
                return icon.sprite;
        return null;
    }

    private TMP_Text CreateLabel(Transform parent, string text)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = labelColor;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 10;
        tmp.fontSizeMax = 28;
        tmp.raycastTarget = false;
        return tmp;
    }
}

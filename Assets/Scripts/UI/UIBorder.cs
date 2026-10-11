using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Hairline frame made of four thin images. (uGUI's Outline effect redraws the whole
/// quad offset in four directions, which washes out translucent panels.)
/// </summary>
public class UIBorder : MonoBehaviour {

    private Image[] edges;

    public Color Color {
        get => edges[0].color;
        set { foreach (Image e in edges) e.color = value; }
    }

    public static UIBorder Add(RectTransform target, Color color, float thickness = 1f) {
        UIBorder b = target.gameObject.AddComponent<UIBorder>();
        b.edges = new[] {
            Edge(target, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, thickness)),   // top
            Edge(target, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, thickness)),   // bottom
            Edge(target, new Vector2(0, 0), new Vector2(0, 1), new Vector2(thickness, 0)),   // left
            Edge(target, new Vector2(1, 0), new Vector2(1, 1), new Vector2(thickness, 0)),   // right
        };
        b.Color = color;
        return b;
    }

    private static Image Edge(RectTransform parent, Vector2 min, Vector2 max, Vector2 size) {
        Image img = UIBuilder.Image(parent, "Edge", Color.white);
        RectTransform rt = img.rectTransform;
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.pivot = new Vector2(min.x == max.x ? min.x : 0.5f, min.y == max.y ? min.y : 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = Vector2.zero;
        img.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        return img;
    }

}

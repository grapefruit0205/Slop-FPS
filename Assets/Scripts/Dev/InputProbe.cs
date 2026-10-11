using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Diagnostics (-debuginput / ?debuginput): log each click and what the UI raycast found under it.</summary>
public class InputProbe : MonoBehaviour {

    private readonly List<RaycastResult> hits = new List<RaycastResult>();

    void Update() {
        if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonUp(0)) return;
        EventSystem es = EventSystem.current;
        string what = "no EventSystem";
        if (es != null) {
            hits.Clear();
            es.RaycastAll(new PointerEventData(es) { position = Input.mousePosition }, hits);
            what = hits.Count > 0 ? hits[0].gameObject.name + " (+" + (hits.Count - 1) + ")" : "nothing";
            what += ", module=" + (es.currentInputModule != null ? es.currentInputModule.GetType().Name : "none");
        }
        Debug.Log("[InputProbe] " + (Input.GetMouseButtonDown(0) ? "down" : "up") + " at " + Input.mousePosition +
                  " screen " + Screen.width + "x" + Screen.height + " -> " + what +
                  " cursor=" + Cursor.lockState + " timeScale=" + Time.timeScale);
    }

}

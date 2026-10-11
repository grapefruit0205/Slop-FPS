// Graphics hooks for GraphicsQuality.cs (WebGL only).
mergeInto(LibraryManager.library, {

  // Canvas resolution in device pixels per CSS pixel. Unity reads Module.devicePixelRatio every frame when it sizes
  // the canvas, so this applies at once. Capped at 1 so a high-DPI screen is not drawn at 2-3x the pixels.
  FpsGfx_SetPixelRatio: function (ratio) {
    Module.devicePixelRatio = Math.min(ratio, window.devicePixelRatio || 1);
  },

  // The WebGL renderer string (unmasked when the browser allows it), to spot software rendering.
  FpsGfx_GpuName: function () {
    var s = "";
    try {
      s = (Module.SystemInfo && Module.SystemInfo.gpu) || "";
      var gl = typeof GLctx !== "undefined" ? GLctx : null;
      if (gl) {
        var ext = gl.getExtension("WEBGL_debug_renderer_info");
        s = gl.getParameter(ext ? ext.UNMASKED_RENDERER_WEBGL : gl.RENDERER) || s;
      }
    } catch (e) { }
    var size = lengthBytesUTF8(s) + 1;
    var buf = _malloc(size);
    stringToUTF8(s, buf, size);
    return buf;
  },

});

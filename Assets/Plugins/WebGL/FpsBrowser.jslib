// Esc is reserved by browsers unless Keyboard Lock is granted. Lock only Escape while fullscreen;
// browsers retain their long-Esc escape hatch. P remains an alternative on unsupported/denied browsers.
mergeInto(LibraryManager.library, {
  FpsBrowser_Init: function () {
    if (Module.fpsBrowserInstalled) return;
    Module.fpsBrowserInstalled = true;
    var keyboard = navigator.keyboard;
    if (!keyboard || !keyboard.lock) return;
    var pending = false, locked = false, denied = false;
    function lockEscape() {
      if (pending || locked || denied) return;
      pending = true;
      keyboard.lock(["Escape"]).then(function () {
        pending = false;
        locked = true;
        // A user may have exited while permission was being requested.
        if (!document.fullscreenElement) { keyboard.unlock(); locked = false; }
      }).catch(function () { pending = false; denied = true; });
    }
    document.addEventListener("fullscreenchange", function () {
      if (document.fullscreenElement) lockEscape();
      else { keyboard.unlock(); locked = false; denied = false; }
    });
    // Keep a user-activation path available for browsers that require it.
    document.addEventListener("click", function (event) {
      if (document.fullscreenElement || (event.target && event.target.id === "unity-fullscreen-button")) lockEscape();
    }, true);
  }
});

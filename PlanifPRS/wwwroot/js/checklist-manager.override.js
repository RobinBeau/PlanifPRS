// Ce patch empêche checklist-manager.js d'écraser la checklist initiale fournie côté serveur.
// Il s'exécute après checklist-manager.js et patche le prototype de ChecklistManager.

(function () {
    function parseJsonSafe(val) {
        if (!val) return null;
        try { return JSON.parse(val); } catch { return null; }
    }

    var hiddenEl = document.getElementById('checklistData');
    var serverInitial = hiddenEl ? parseJsonSafe(hiddenEl.value) : null;

    if (!serverInitial) {
        console.warn('[EDIT][CHK-PATCH] Aucune valeur serveur détectée dans #checklistData, patch non appliqué.');
        return;
    }

    // Si la valeur serveur est significative (type défini ou éléments existants), on patche.
    var hasMeaningfulData = !!(serverInitial.type || (serverInitial.elements && serverInitial.elements.length > 0));
    if (!hasMeaningfulData) {
        console.warn('[EDIT][CHK-PATCH] Valeur serveur vide, patch inutile.');
        return;
    }

    var attempts = 0;
    var maxAttempts = 100; // ~5s à 50ms
    var timer = setInterval(function () {
        attempts++;
        if (window.ChecklistManager && window.ChecklistManager.prototype) {
            try {
                var proto = window.ChecklistManager.prototype;
                if (proto.__editPatchApplied) {
                    clearInterval(timer);
                    return;
                }

                var originalInit = proto.init;
                proto.init = function () {
                    try {
                        // Injecter la checklist serveur AVANT les resets internes
                        this.currentChecklist = JSON.parse(JSON.stringify(serverInitial));
                        if (hiddenEl) hiddenEl.value = JSON.stringify(this.currentChecklist);

                        var editor = document.getElementById('checklistEditor');
                        var counter = document.getElementById('checklistItemCount');
                        if (editor) editor.style.display = '';
                        if (counter) counter.textContent = String((this.currentChecklist.elements || []).length);

                        this.__preserveServerChecklist = true;
                        console.log('[EDIT][CHK-PATCH] Checklist initiale injectée et préservée (init).');
                    } catch (e) {
                        console.error('[EDIT][CHK-PATCH] Erreur pré-init:', e);
                    }
                    // Appel de l’init d’origine
                    var res = originalInit && originalInit.apply(this, arguments);
                    return res;
                };

                // Si le manager possède une méthode de changement de type, on la patche pour éviter les resets indésirables
                if (typeof proto.handleTypeChange === 'function') {
                    var originalHandleTypeChange = proto.handleTypeChange;
                    proto.handleTypeChange = function () {
                        // Si on a une checklist serveur et que le manager tente de partir d’un objet vide, on restaure.
                        try {
                            if (this.__preserveServerChecklist) {
                                if (!this.currentChecklist || !this.currentChecklist.elements || this.currentChecklist.elements.length === 0) {
                                    this.currentChecklist = JSON.parse(JSON.stringify(serverInitial));
                                    if (hiddenEl) hiddenEl.value = JSON.stringify(this.currentChecklist);
                                    console.log('[EDIT][CHK-PATCH] Checklist serveur restaurée (handleTypeChange).');
                                }
                            }
                        } catch (e) {
                            console.error('[EDIT][CHK-PATCH] Erreur handleTypeChange:', e);
                        }
                        return originalHandleTypeChange.apply(this, arguments);
                    };
                }

                // Optionnel: si le manager a une méthode reset ou similaire, on peut la sécuriser aussi
                if (typeof proto.resetChecklist === 'function') {
                    var originalReset = proto.resetChecklist;
                    proto.resetChecklist = function () {
                        var result = originalReset.apply(this, arguments);
                        try {
                            if (this.__preserveServerChecklist) {
                                this.currentChecklist = JSON.parse(JSON.stringify(serverInitial));
                                if (hiddenEl) hiddenEl.value = JSON.stringify(this.currentChecklist);
                                console.log('[EDIT][CHK-PATCH] Checklist serveur restaurée (resetChecklist).');
                            }
                        } catch (e) {
                            console.error('[EDIT][CHK-PATCH] Erreur resetChecklist:', e);
                        }
                        return result;
                    };
                }

                proto.__editPatchApplied = true;
                clearInterval(timer);
                console.log('[EDIT][CHK-PATCH] Patch ChecklistManager appliqué.');
            } catch (err) {
                clearInterval(timer);
                console.error('[EDIT][CHK-PATCH] Echec du patch:', err);
            }
        } else if (attempts >= maxAttempts) {
            clearInterval(timer);
            console.warn('[EDIT][CHK-PATCH] ChecklistManager non disponible, patch abandonné.');
        }
    }, 50);
})();
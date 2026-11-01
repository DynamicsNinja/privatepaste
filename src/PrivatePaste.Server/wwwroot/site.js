"use strict";

const csrfToken = () => document.querySelector('meta[name="csrf-token"]')?.content ?? "";
const PROTOCOL = { "X-PrivatePaste-Protocol": "2" };

// The history rows are built and decrypted by js/history.js, which holds the vault key; its versioned URL is on the
// page, and importing that same URL again gives the instance the page already runs.
const historyModule = () => import(document.getElementById("history").dataset.history);

const apiCall = (path, method) => fetch(path, {
    method,
    headers: { ...PROTOCOL, RequestVerificationToken: csrfToken() },
}).then(async (response) => {
    // Encryption was reset while this tab was open: js/vault-ui.js forgets the key and asks for the new passphrase.
    if (response.status === 409) {
        const problem = await response.clone().json().catch(() => null);
        if (problem?.title === "Encryption was reset") document.dispatchEvent(new Event("pp:vault-reset"));
    }
    return response;
});

// Copy: POST restore (re-syncs the clip to the other devices), decrypt it, then put it on the browser clipboard.
// navigator.clipboard.write() must be called synchronously in the click handler (Safari drops user activation
// across awaits), so the ClipboardItem carries a promise that resolves after the restore round-trip and decryption.
document.addEventListener("click", (event) => {
    const button = event.target.closest("button.copy");
    if (!button) return;

    const row = button.closest(".clip");
    const id = row.dataset.id;
    const isImage = row.dataset.kind === "image";
    // An icon button: the result shows as an icon change (CSS) and in its accessible name and tooltip.
    const label = button.getAttribute("aria-label");
    const setLabel = (text) => {
        button.setAttribute("aria-label", text);
        button.title = text;
    };
    button.disabled = true;

    const restored = apiCall(`/api/clips/${encodeURIComponent(id)}/restore`, "POST").then((response) => {
        if (!response.ok) throw new Error(`Restore failed (${response.status})`);
        return response.json();
    });
    // A Blob: text/plain, or image/png for an image clip (its blob fetched and decrypted).
    const content = Promise.all([historyModule(), restored]).then(([history, clip]) => history.clipContent(clip));

    let done;
    if (window.ClipboardItem && navigator.clipboard?.write) {
        done = navigator.clipboard.write([new ClipboardItem({ [isImage ? "image/png" : "text/plain"]: content })]);
    } else if (!isImage) {
        done = content.then((blob) => blob.text()).then((text) => navigator.clipboard.writeText(text));
    } else {
        done = Promise.reject(new Error("Clipboard image write is not supported in this browser."));
    }

    done.then(() => {
        setLabel("Copied");
        button.classList.add("done");
    })
        .catch((error) => {
            console.error(error);
            setLabel("Copy failed");
            button.classList.add("failed");
        })
        .finally(() => {
            setTimeout(() => {
                setLabel(label);
                button.classList.remove("done", "failed");
                button.disabled = false;
            }, 1500);
        });
});

// Pin and Unpin: the toggle's aria-pressed says which; the row is updated from the clip the server sends back.
document.addEventListener("click", (event) => {
    const button = event.target.closest("button.pin-toggle");
    if (!button) return;
    const id = button.closest(".clip").dataset.id;
    const pinned = button.getAttribute("aria-pressed") === "true";
    button.disabled = true;
    apiCall(`/api/clips/${encodeURIComponent(id)}/pin`, pinned ? "DELETE" : "POST")
        .then((response) => {
            if (!response.ok) throw new Error(`Pin failed (${response.status})`);
            return Promise.all([historyModule(), response.json()]);
        })
        .then(([history, clip]) => {
            history.clipChanged(clip);
            button.disabled = false;
        })
        .catch((error) => {
            // Shown like a failed Copy: the button turns red for a moment and says so to screen readers and on hover.
            console.error(error);
            const name = button.querySelector(".sr-only");
            const label = name.textContent;
            name.textContent = button.title = `${label} failed`;
            button.classList.add("failed");
            setTimeout(() => {
                name.textContent = label;
                button.removeAttribute("title");
                button.classList.remove("failed");
                button.disabled = false;
            }, 1500);
        });
});

// Delete: a clip already gone (404) leaves the list too.
document.addEventListener("click", (event) => {
    const button = event.target.closest("button.delete");
    if (!button) return;
    const id = button.closest(".clip").dataset.id;
    button.disabled = true;
    apiCall(`/api/clips/${encodeURIComponent(id)}`, "DELETE")
        .then((response) => {
            if (!response.ok && response.status !== 404) throw new Error(`Delete failed (${response.status})`);
            return historyModule();
        })
        .then((history) => history.clipRemoved(id))
        .catch((error) => {
            console.error(error);
            button.disabled = false;
        });
});

// Lock: forget this tab's vault key (js/vault-ui.js stores it under this name). Shown only while the key is here;
// the history page listens for "pp:locked" to ask for the passphrase again. Signing out forgets it too.
const VAULT_KEY = "pp.vault";
const forgetVaultKey = () => {
    try { sessionStorage.removeItem(VAULT_KEY); } catch { /* nothing stored */ }
};
const lockButton = document.getElementById("lock");
if (lockButton) {
    try { lockButton.hidden = !sessionStorage.getItem(VAULT_KEY); } catch { lockButton.hidden = true; }
    lockButton.addEventListener("click", () => {
        forgetVaultKey();
        lockButton.hidden = true;
        document.dispatchEvent(new Event("pp:locked"));
    });
}
document.getElementById("sign-out")?.addEventListener("submit", forgetVaultKey);

// Settings: save auto-delete as soon as the choice changes.
document.getElementById("auto-delete")?.addEventListener("change", (event) => {
    const select = event.target;
    const status = document.getElementById("auto-delete-status");
    status.textContent = "";
    fetch("/api/settings", {
        method: "PUT",
        headers: { ...PROTOCOL, RequestVerificationToken: csrfToken(), "Content-Type": "application/json" },
        body: JSON.stringify({ autoDeleteDays: select.value === "" ? null : Number(select.value) }),
    })
        .then((response) => {
            if (!response.ok) throw new Error(`Save failed (${response.status})`);
            status.textContent = "Saved";
            setTimeout(() => { status.textContent = ""; }, 2500);
        })
        .catch((error) => {
            console.error(error);
            status.textContent = "Couldn't save";
        });
});

// Clear history: confirm with the number of clips that would go.
document.addEventListener("submit", (event) => {
    const message = event.target.dataset?.confirm;
    if (message && !window.confirm(message)) event.preventDefault();
});

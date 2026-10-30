"use strict";

// Copy: POST restore (re-syncs the clip to the other devices), then put it on the browser clipboard.
// navigator.clipboard.write() must be called synchronously in the click handler (Safari drops user activation
// across awaits), so the ClipboardItem carries promises that resolve after the restore round-trip.
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
    const token = document.querySelector('meta[name="csrf-token"]')?.content ?? "";
    button.disabled = true;

    const restored = fetch(`/api/clips/${id}/restore`, {
        method: "POST",
        headers: { RequestVerificationToken: token },
    }).then((response) => {
        if (!response.ok) throw new Error(`Restore failed (${response.status})`);
        return response.json();
    });

    let done;
    if (window.ClipboardItem && navigator.clipboard?.write) {
        const item = isImage
            ? new ClipboardItem({
                "image/png": restored
                    .then(() => fetch(`/api/clips/${id}/image`))
                    .then((response) => {
                        if (!response.ok) throw new Error(`Image fetch failed (${response.status})`);
                        return response.blob();
                    }),
            })
            : new ClipboardItem({
                "text/plain": restored.then((clip) => new Blob([clip.text ?? ""], { type: "text/plain" })),
            });
        done = navigator.clipboard.write([item]);
    } else if (!isImage) {
        done = restored.then((clip) => navigator.clipboard.writeText(clip.text ?? ""));
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

// Settings: save auto-delete as soon as the choice changes.
document.getElementById("auto-delete")?.addEventListener("change", (event) => {
    const select = event.target;
    const status = document.getElementById("auto-delete-status");
    const token = document.querySelector('meta[name="csrf-token"]')?.content ?? "";
    status.textContent = "";
    fetch("/api/settings", {
        method: "PUT",
        headers: { RequestVerificationToken: token, "Content-Type": "application/json" },
        body: JSON.stringify({ autoDeleteDays: select.value === "" ? null : Number(select.value) }),
    })
        .then((response) => {
            if (!response.ok) throw new Error(`Save failed (${response.status})`);
            status.textContent = "Saved";
            setTimeout(() => { status.textContent = ""; }, 2500);
        })
        .catch((error) => {
            console.error(error);
            status.textContent = "Could not save";
        });
});

// Clear history: confirm with the number of clips that would go.
document.addEventListener("submit", (event) => {
    const message = event.target.dataset?.confirm;
    if (message && !window.confirm(message)) event.preventDefault();
});

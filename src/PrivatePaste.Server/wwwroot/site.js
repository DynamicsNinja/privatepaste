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
    const label = button.textContent;
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
        button.textContent = "Copied";
        button.classList.add("done");
    })
        .catch((error) => {
            console.error(error);
            button.textContent = "Failed";
        })
        .finally(() => {
            setTimeout(() => {
                button.textContent = label;
                button.classList.remove("done");
                button.disabled = false;
            }, 1500);
        });
});

"use strict";

// Copy: POST restore (re-syncs the clip to the other devices), then put it on the browser clipboard.
document.addEventListener("click", async (event) => {
    const button = event.target.closest("button.copy");
    if (!button) return;

    const row = button.closest(".clip");
    const label = button.textContent;
    const token = document.querySelector('meta[name="csrf-token"]')?.content ?? "";
    button.disabled = true;
    try {
        const response = await fetch(`/api/clips/${row.dataset.id}/restore`, {
            method: "POST",
            headers: { RequestVerificationToken: token },
        });
        if (!response.ok) throw new Error(`Restore failed (${response.status})`);
        const clip = await response.json();

        if (row.dataset.kind === "image") {
            const image = await fetch(`/api/clips/${row.dataset.id}/image`);
            if (!image.ok) throw new Error(`Image fetch failed (${image.status})`);
            await navigator.clipboard.write([new ClipboardItem({ "image/png": await image.blob() })]);
        } else {
            await navigator.clipboard.writeText(clip.text ?? "");
        }
        button.textContent = "Copied";
    } catch (error) {
        console.error(error);
        button.textContent = "Failed";
    } finally {
        setTimeout(() => {
            button.textContent = label;
            button.disabled = false;
        }, 1500);
    }
});

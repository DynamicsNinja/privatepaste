"use strict";

// A hairline under the sticky top bar once the page has scrolled.
const bar = document.querySelector(".bar");
const markScrolled = () => bar.classList.toggle("scrolled", window.scrollY > 4);
window.addEventListener("scroll", markScrolled, { passive: true });
markScrolled();

// Phone menu: the button opens and closes the links; picking one, Escape or a tap elsewhere closes it.
const menuButton = document.querySelector(".menu-button");
const menu = document.getElementById("site-nav");

function setMenu(open) {
    menu.classList.toggle("open", open);
    menuButton.setAttribute("aria-expanded", open);
}

menuButton.addEventListener("click", () => setMenu(!menu.classList.contains("open")));
menu.addEventListener("click", (event) => { if (event.target.closest("a")) setMenu(false); });
document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && menu.classList.contains("open")) {
        setMenu(false);
        menuButton.focus();
    }
});
document.addEventListener("click", (event) => {
    if (!event.target.closest(".top")) setMenu(false);
});

// Deploy tabs: buttons switch the panels; arrow keys move between tabs (WAI-ARIA tabs pattern).
const tabs = [...document.querySelectorAll('[role="tab"]')];

function selectTab(tab, focus) {
    for (const t of tabs) {
        const on = t === tab;
        t.setAttribute("aria-selected", on);
        t.tabIndex = on ? 0 : -1;
        document.getElementById(t.getAttribute("aria-controls")).hidden = !on;
    }
    if (focus) tab.focus();
}

for (const tab of tabs) {
    tab.addEventListener("click", () => selectTab(tab, false));
    tab.addEventListener("keydown", (event) => {
        const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
        if (!step) return;
        event.preventDefault();
        selectTab(tabs[(tabs.indexOf(tab) + step + tabs.length) % tabs.length], true);
    });
}

// Phone carousel: the dots show the slide in the middle and scroll to theirs; arrow keys step through the slides
// while the carousel has the focus. Without JavaScript it is a plain swipeable strip (the dots stay hidden).
const carousel = document.querySelector(".carousel");
const strip = carousel.querySelector(".slides");
const slides = [...strip.querySelectorAll(".slide")];
const dots = [...carousel.querySelectorAll(".dots button")];
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
let currentSlide = 0;
// While a dot or arrow key scrolls the strip, the slides passed on the way don't take over the dots.
let targetSlide = -1;

function markSlide(index) {
    currentSlide = index;
    dots.forEach((dot, i) => {
        if (i === index) dot.setAttribute("aria-current", "true");
        else dot.removeAttribute("aria-current");
    });
}

function showSlide(index) {
    const slide = slides[index];
    const left = slide.offsetLeft - (strip.clientWidth - slide.offsetWidth) / 2;
    if (Math.abs(left - strip.scrollLeft) > 1) targetSlide = index;
    strip.scrollTo({ left, behavior: reducedMotion.matches ? "auto" : "smooth" });
    markSlide(index);
}

// Slides 2 and 3 wait in <noscript> until the carousel is first touched, scrolled or focused, so a visitor who
// never swipes doesn't download them. (Listened for in the capture phase: a scroll on the strip doesn't bubble.)
function loadSlides() {
    for (const hidden of strip.querySelectorAll("noscript")) hidden.outerHTML = hidden.textContent;
}
for (const type of ["pointerdown", "touchstart", "scroll", "focusin"]) {
    carousel.addEventListener(type, loadSlides, { once: true, passive: true, capture: true });
}

let slideFrame = 0;
strip.addEventListener("scroll", () => {
    cancelAnimationFrame(slideFrame);
    slideFrame = requestAnimationFrame(() => {
        const middle = strip.scrollLeft + strip.clientWidth / 2;
        const distance = (slide) => Math.abs(slide.offsetLeft + slide.offsetWidth / 2 - middle);
        const nearest = slides.reduce((best, slide, i) => (distance(slide) < distance(slides[best]) ? i : best), 0);
        if (targetSlide >= 0) {
            if (nearest === targetSlide) targetSlide = -1;
            return;
        }
        if (nearest !== currentSlide) markSlide(nearest);
    });
}, { passive: true });
strip.addEventListener("pointerdown", () => { targetSlide = -1; }, { passive: true });

dots.forEach((dot, i) => dot.addEventListener("click", () => showSlide(i)));
carousel.addEventListener("keydown", (event) => {
    const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    showSlide(Math.min(slides.length - 1, Math.max(0, currentSlide + step)));
});

// Copy buttons on every code block, in its header strip so they never cover the code. They copy the text exactly
// as written, even where a narrow screen wraps the lines.
for (const pre of document.querySelectorAll("pre[data-copy]")) {
    const text = pre.textContent;
    // One block per line with a hanging indent, so a line that wraps on a narrow screen continues under its own
    // text instead of at the left edge (where YAML would look like it changed level).
    if (text.includes("\n")) {
        pre.replaceChildren(...text.split("\n").map((line) => {
            const span = document.createElement("span");
            span.className = "line";
            span.textContent = line;
            span.style.setProperty("--hang", `${line.match(/^ */)[0].length + 2}ch`);
            return span;
        }));
    }
    const button = document.createElement("button");
    button.type = "button";
    button.className = "copy";
    button.textContent = "Copy";
    button.addEventListener("click", async () => {
        try {
            await navigator.clipboard.writeText(text);
            button.textContent = "Copied";
        } catch {
            button.textContent = "Select and copy";
        }
        setTimeout(() => { button.textContent = "Copy"; }, 1500);
    });
    pre.parentElement.querySelector(".code-head").append(button);
}

// Download buttons: release files carry the version in their names, so look up the newest desktop and Android
// releases (GitHub's release list, cached by this site's nginx as /releases.json) and link straight to the files.
// The links go through this site's /download/ (nginx fetches the file from GitHub), because a browser only
// honours the download attribute on a same-origin link: a phone saves the file instead of opening GitHub.
// Until that answers, or if it can't, they open the Releases page.
const assetFor = {
    windows: /-win-x64\.msi$/,
    macos: /-osx-arm64\.zip$/,
    "macos-x64": /-osx-x64\.zip$/,
    linux: /-linux-x64\.tar\.gz$/,
    android: /\.apk$/,
};
const tagFor = { windows: "desktop-v", macos: "desktop-v", "macos-x64": "desktop-v", linux: "desktop-v", android: "android-v" };

fetch("/releases.json")
    .then((response) => (response.ok ? response.json() : []))
    .then((releases) => {
        for (const link of document.querySelectorAll("[data-platform]")) {
            const platform = link.dataset.platform;
            const release = releases.find((r) => !r.draft && !r.prerelease && r.tag_name.startsWith(tagFor[platform]));
            const asset = release?.assets.find((a) => assetFor[platform].test(a.name));
            if (asset) {
                link.href = `/download/${encodeURIComponent(release.tag_name)}/${encodeURIComponent(asset.name)}`;
                link.download = asset.name;
                link.title = `${asset.name} (${release.tag_name.replace(/^\w+-v/, "version ")})`;
            }
        }
    })
    .catch(() => { /* the Releases page links stay */ });

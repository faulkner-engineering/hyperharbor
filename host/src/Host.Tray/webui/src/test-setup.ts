// jsdom has no Web Animations API, which Svelte transitions use. A stand-in that finishes at once
// lets components with transitions render in tests.
if (typeof Element !== "undefined" && !Element.prototype.animate) {
  Element.prototype.animate = function animate() {
    const animation = {
      onfinish: null as ((event: unknown) => void) | null,
      cancel() {},
      finish() {},
      finished: Promise.resolve(),
      currentTime: 0,
    };
    setTimeout(() => animation.onfinish?.({}));
    return animation as unknown as Animation;
  };
}

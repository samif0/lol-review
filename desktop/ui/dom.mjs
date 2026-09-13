// Shared primitives keep page renderers focused on their own data and behavior.
export const $ = (id) => document.getElementById(id);
export function show(element, visible) { if (element) element.hidden = !visible; }
export function clear(element) { while (element?.firstChild) element.removeChild(element.firstChild); }
export function tpl(id) { return $(id).content.firstElementChild.cloneNode(true); }

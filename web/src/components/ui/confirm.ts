// A native modal stays above record dialogs, traps focus and restores it on close.

import type { SyntheticEvent } from "react";

/**
 * The Escape handler of a dialog. Dismissing a file picker fires a cancel event on its file input that bubbles up to
 * the dialog around it; only the dialog's own cancel closes it.
 */
export function onDialogCancel(close: () => void) {
  return (event: SyntheticEvent<HTMLDialogElement>) => {
    if (event.target !== event.currentTarget) return;
    event.preventDefault();
    close();
  };
}

const pending = new Map<string, Promise<boolean>>();
export function confirmAction(message: string): Promise<boolean> {
  const existing = pending.get(message);
  if (existing) return existing;
  const result = new Promise<boolean>((resolve) => {
    const previous = document.activeElement as HTMLElement | null;
    const dialog = document.createElement("dialog");
    dialog.className = "confirmation-dialog";
    const title = document.createElement("h2");
    title.id = `confirm-${crypto.randomUUID()}`;
    title.textContent = "Confirm action";
    dialog.setAttribute("aria-labelledby", title.id);
    const description = document.createElement("p");
    description.textContent = message;
    const actions = document.createElement("div");
    actions.className = "dialog-footer";
    const cancel = document.createElement("button");
    cancel.className = "button secondary";
    cancel.textContent = "Cancel";
    const accept = document.createElement("button");
    accept.className = "button primary";
    accept.textContent = "Continue";
    let finished = false;
    const finish = (answer: boolean) => {
      if (finished) return;
      finished = true;
      pending.delete(message);
      dialog.close();
      dialog.remove();
      previous?.focus();
      resolve(answer);
    };
    cancel.onclick = () => finish(false);
    accept.onclick = () => finish(true);
    dialog.oncancel = (event) => {
      event.preventDefault();
      finish(false);
    };
    actions.append(cancel, accept);
    dialog.append(title, description, actions);
    document.body.append(dialog);
    queueMicrotask(() => {
      dialog.showModal();
      cancel.focus();
    });
  });
  pending.set(message, result);
  return result;
}
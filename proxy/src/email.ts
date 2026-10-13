/**
 * Resend integration. Sends the magic-link / one-time-code email.
 */

export async function sendMagicLinkEmail(
  resendApiKey: string,
  from: string,
  appName: string,
  to: string,
  code: string,
  purpose: "signup" | "login" | "web",
): Promise<void> {
  const subject = purpose === "web"
    ? `Your ${appName} sign-in code`
    : purpose === "signup"
    ? `Your ${appName} signup code`
    : `Your ${appName} login code`;

  const body = purpose === "web"
    ? `Enter this code on revu.lol to sign in to ${appName}:`
    : purpose === "signup"
    ? `Welcome to ${appName}. Paste this code into the desktop app to finish signing up:`
    : `Paste this code into the ${appName} desktop app to finish logging in:`;

  const expiryLine = "This code expires in 10 minutes and can only be used once. If you did not request it, you can safely ignore this email.";
  // Web codes are typed into a website, so the web email also warns against phishing.
  const phishingLine = "Only enter this code on revu.lol or in the Revu app. Revu will never ask you to share it, and never asks for a password.";
  const webExtraHtml = purpose === "web"
    ? `\n      <p style="color: #666; font-size: 13px;">${phishingLine}</p>`
    : "";

  const html = `
    <div style="font-family: -apple-system, Segoe UI, sans-serif; color: #222; max-width: 480px; margin: 0 auto;">
      <h2 style="color: #111;">${appName}</h2>
      <p>${body}</p>
      <p style="font-family: ui-monospace, Consolas, monospace; font-size: 28px; font-weight: bold; letter-spacing: 4px; background: #f4f4f8; padding: 14px 18px; border-radius: 6px; text-align: center;">${code}</p>
      <p style="color: #666; font-size: 13px;">${expiryLine}</p>${webExtraHtml}
    </div>
  `;

  const plain = purpose === "web"
    ? `${body}\n\n${code}\n\n${expiryLine}\n\n${phishingLine}`
    : `${body}\n\n${code}\n\nThis code expires in 10 minutes.`;

  const res = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${resendApiKey}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      from,
      to: [to],
      subject,
      html,
      text: plain,
    }),
  });

  if (!res.ok) {
    // Drain the body so the connection is released, but never include it: a
    // Resend error body can echo the recipient address into the logs.
    await res.text().catch(() => "");
    throw new Error(`resend_failed: ${res.status}`);
  }
}

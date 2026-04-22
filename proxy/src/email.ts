/**
 * Resend integration. Sends the magic-link / one-time-code email.
 */

export async function sendMagicLinkEmail(
  resendApiKey: string,
  from: string,
  appName: string,
  to: string,
  code: string,
  purpose: "signup" | "login",
): Promise<void> {
  const subject = purpose === "signup"
    ? `Your ${appName} signup code`
    : `Your ${appName} login code`;

  const body = purpose === "signup"
    ? `Welcome to ${appName}. Paste this code into the desktop app to finish signing up:`
    : `Paste this code into the ${appName} desktop app to finish logging in:`;

  const html = `
    <div style="font-family: -apple-system, Segoe UI, sans-serif; color: #222; max-width: 480px; margin: 0 auto;">
      <h2 style="color: #111;">${appName}</h2>
      <p>${body}</p>
      <p style="font-family: ui-monospace, Consolas, monospace; font-size: 28px; font-weight: bold; letter-spacing: 4px; background: #f4f4f8; padding: 14px 18px; border-radius: 6px; text-align: center;">${code}</p>
      <p style="color: #666; font-size: 13px;">This code expires in 10 minutes and can only be used once. If you did not request it, you can safely ignore this email.</p>
    </div>
  `;

  const plain = `${body}\n\n${code}\n\nThis code expires in 10 minutes.`;

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
    const text = await res.text();
    throw new Error(`resend_failed: ${res.status} ${text.slice(0, 200)}`);
  }
}

Kopi Kilat - Landing page (static)

Files:
- apps/kopi-kilat/index.html  (single-file landing page)

Quick verification:
1. Open apps/kopi-kilat/index.html in a browser (double-click or open from IDE).
2. Verify sections are present: Hero, 3 Keunggulan, Cara pesan, Testimoni, CTA.
3. Click "Pesan lewat WhatsApp" — it opens a new tab to wa.me with placeholder number + prefilled message.
4. To test mobile view, open DevTools (F12) and toggle device toolbar or open the file on a phone.

How to edit:
- Edit apps/kopi-kilat/index.html. Near the bottom in the <script> block change phoneNumber and prefilledMessage.
- Testimonials and keunggulan are in the HTML and can be edited directly.

Notes/limitations:
- WhatsApp number is a placeholder (62XXXXXXXXX). Replace with real number in the script.
- This is a single-file lightweight page without analytics or server-side components.
- Testimoni are simulated placeholders (marked in the page).

Next steps (optional):
- Add small SVG icons as inline elements if desired.
- Add microcopy A/B variants or localized pricing.
- Implement real-time outlet routing backend (future work).

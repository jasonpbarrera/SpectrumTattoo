const STUDIO_PIN = "4110";
const KV_KEY = "bookings_v1";

const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type, X-Studio-Pin",
  "Access-Control-Max-Age": "86400"
};

function json(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      "Content-Type": "application/json",
      ...CORS_HEADERS
    }
  });
}

async function readBookings(env) {
  const raw = await env.BOOKINGS_KV.get(KV_KEY);
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

async function writeBookings(env, list) {
  await env.BOOKINGS_KV.put(KV_KEY, JSON.stringify(list));
}

function getScheduleForDate(dateStr) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(dateStr || "");
  if (!match) return { isOpen: false, note: "Select a valid date.", times: [] };

  const year = Number(match[1]);
  const month = Number(match[2]) - 1;
  const day = Number(match[3]);
  const d = new Date(Date.UTC(year, month, day));
  const dow = d.getUTCDay(); // 0=Sun, 1=Mon, 2=Tue, ... 6=Sat

  if (dow === 1 || dow === 2) {
    return {
      isOpen: false,
      note: "Studio is closed Mondays & Tuesdays. Please select Wednesday–Sunday.",
      times: []
    };
  }

  if (dow === 6) {
    return {
      isOpen: true,
      note: "Saturday Hours: 10:00 AM – 4:00 PM",
      times: ["10:00 AM", "11:30 AM", "1:00 PM", "2:30 PM"]
    };
  }

  if (dow === 0) {
    return {
      isOpen: true,
      note: "Sunday Hours: 10:00 AM – 3:00 PM",
      times: ["10:00 AM", "11:30 AM", "1:00 PM"]
    };
  }

  return {
    isOpen: true,
    note: "Studio Hours: 10:00 AM – 6:00 PM",
    times: ["10:00 AM", "11:30 AM", "1:00 PM", "2:30 PM", "4:00 PM"]
  };
}

function makeId() {
  const chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  let out = "ST-";
  for (let i = 0; i < 6; i++) {
    out += chars[Math.floor(Math.random() * chars.length)];
  }
  return out;
}

export default {
  async fetch(request, env) {
    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: CORS_HEADERS });
    }

    const url = new URL(request.url);
    const op = (url.searchParams.get("op") || "").toLowerCase();

    if (request.method === "GET") {
      const bookings = await readBookings(env);

      if (op === "availability") {
        const artist = url.searchParams.get("artist") || "First Available Artist";
        const dateStr = url.searchParams.get("date") || "";
        const sched = getScheduleForDate(dateStr);
        if (!sched.isOpen) {
          return json({ ok: true, isOpen: false, note: sched.note, slots: [] });
        }

        const taken = new Set(
          bookings
            .filter(
              (b) =>
                String(b.date || "").toLowerCase() === dateStr.toLowerCase() &&
                (String(b.artist || "").toLowerCase() === artist.toLowerCase() ||
                  artist.toLowerCase() === "first available artist") &&
                (b.status === "Confirmed" || b.status === "ApprovedAwaitingDeposit")
            )
            .map((b) => String(b.timeSlot || "").toLowerCase())
        );

        const slots = sched.times.map((t) => ({
          time: t,
          available: !taken.has(t.toLowerCase())
        }));

        return json({ ok: true, isOpen: true, note: sched.note, slots });
      }

      const id = url.searchParams.get("id");
      if (id) {
        const found = bookings.find(
          (b) => String(b.id || "").toLowerCase() === id.trim().toLowerCase()
        );
        return found ? json({ ok: true, booking: found }) : json({ ok: false, error: "Not found" }, 404);
      }

      // Admin list requires X-Studio-Pin
      const pin = (request.headers.get("X-Studio-Pin") || "").trim();
      if (pin !== STUDIO_PIN) {
        return json({ ok: false, error: "Unauthorized" }, 401);
      }

      const sorted = [...bookings].sort(
        (a, b) => new Date(b.createdAt || 0).getTime() - new Date(a.createdAt || 0).getTime()
      );
      return json({ ok: true, bookings: sorted });
    }

    if (request.method === "POST") {
      let body;
      try {
        body = await request.json();
      } catch {
        return json({ ok: false, error: "Invalid JSON" }, 400);
      }

      const postOp = String(body.op || "").toLowerCase();
      const bookings = await readBookings(env);

      if (postOp === "seed") {
        const pin = (request.headers.get("X-Studio-Pin") || "").trim();
        if (pin !== STUDIO_PIN) return json({ ok: false, error: "Unauthorized" }, 401);

        if (bookings.length === 0) {
          const d = new Date(Date.now() + 86400000);
          while (d.getUTCDay() === 1 || d.getUTCDay() === 2) {
            d.setUTCDate(d.getUTCDate() + 1);
          }
          const iso = d.toISOString().slice(0, 10);
          bookings.push({
            id: "ST-DEMO01",
            artist: "Dawn Yermasek",
            date: iso,
            timeSlot: "11:30 AM",
            serviceType: "Custom Tattoo",
            name: "Sample Client",
            phone: "(423) 555-0142",
            email: "client@example.com",
            placement: "Outer forearm, ~5 in",
            ideaDescription: "Watercolor luna moth with fine-line fern botanicals.",
            status: "PendingApproval",
            depositAmountCents: 5000,
            depositPaid: false,
            artistNote: null,
            createdAt: new Date().toISOString()
          });
          await writeBookings(env, bookings);
        }
        return json({ ok: true, bookings });
      }

      if (postOp === "approve" || postOp === "confirm" || postOp === "decline" || postOp === "simulate-deposit") {
        if (postOp !== "simulate-deposit") {
          const pin = (request.headers.get("X-Studio-Pin") || "").trim();
          if (pin !== STUDIO_PIN) return json({ ok: false, error: "Unauthorized" }, 401);
        }

        const targetId = String(body.id || "").trim().toLowerCase();
        const item = bookings.find((b) => String(b.id || "").toLowerCase() === targetId);
        if (!item) return json({ ok: false, error: "Booking not found" }, 404);

        if (postOp === "approve") {
          item.status = "ApprovedAwaitingDeposit";
          item.depositPaid = false;
        } else if (postOp === "confirm" || postOp === "simulate-deposit") {
          item.status = "Confirmed";
          item.depositPaid = true;
        } else if (postOp === "decline") {
          item.status = "Declined";
          item.depositPaid = false;
        }

        if (typeof body.artistNote === "string") item.artistNote = body.artistNote;
        if (typeof body.depositAmountCents === "number" && body.depositAmountCents >= 1000) {
          item.depositAmountCents = body.depositAmountCents;
        }

        await writeBookings(env, bookings);
        return json({ ok: true, booking: item });
      }

      // Create new booking
      const artist = String(body.artist || "First Available Artist").trim();
      const dateStr = String(body.date || "").trim();
      const timeSlot = String(body.timeSlot || "").trim();
      const sched = getScheduleForDate(dateStr);
      if (!sched.isOpen || !sched.times.includes(timeSlot)) {
        return json({ ok: false, error: "Selected date or time slot is not open." }, 400);
      }

      const conflict = bookings.some(
        (b) =>
          String(b.date || "").toLowerCase() === dateStr.toLowerCase() &&
          String(b.timeSlot || "").toLowerCase() === timeSlot.toLowerCase() &&
          (String(b.artist || "").toLowerCase() === artist.toLowerCase() ||
            artist.toLowerCase() === "first available artist") &&
          (b.status === "Confirmed" || b.status === "ApprovedAwaitingDeposit")
      );
      if (conflict) {
        return json({ ok: false, error: "That time slot was just booked. Please pick another slot." }, 409);
      }

      const record = {
        id: makeId(),
        artist,
        date: dateStr,
        timeSlot,
        serviceType: String(body.serviceType || "Custom Tattoo").trim(),
        name: String(body.name || "").trim(),
        phone: String(body.phone || "").trim(),
        email: String(body.email || "").trim(),
        placement: String(body.placement || "").trim(),
        ideaDescription: String(body.ideaDescription || "").trim(),
        status: "PendingApproval",
        depositAmountCents: 5000,
        depositPaid: false,
        artistNote: null,
        createdAt: new Date().toISOString()
      };

      bookings.unshift(record);
      await writeBookings(env, bookings);
      return json({ ok: true, booking: record }, 201);
    }

    return json({ ok: false, error: "Method not allowed" }, 405);
  }
};

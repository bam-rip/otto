using System.Reflection;

namespace Otto;

/// Debug only (--render-ui): draws parts of the panel to PNGs with sample content, so layout can be checked
/// without opening the real panel (which another running copy of Otto may own).
static class RenderUi
{
    public static void Run()
    {
        using var panel = new ChatPanel { Size = new Size(420, 820) };
        panel.CreateControl();
        _ = panel.Handle; // the real panel has a window handle, which is when it picks its background
        var chat = panel.Controls.OfType<ChatView>().Single();
        var history = panel.History;
        chat.SetBounds(0, 0, panel.Width, 640);
        history.SetBounds(0, 0, panel.Width, 640);

        chat.AddMessage("Find me the cheapest flights from Brisbane to Tokyo in April and put them in a spreadsheet", user: true);
        chat.AddNote("Searching “Brisbane to Tokyo flights April”", tool: true);
        chat.AddMessage("I found three options. The cheapest is Jetstar via Cairns at $612 return, then ZIPAIR direct at $689, then Qantas direct at $1,140. They're in Flights.xlsx on your desktop, sorted by price, with links to book each one.", user: false);
        chat.AddMessage("thanks", user: true);
        Hover(chat, new Point(60, 250)); // over Otto's wide reply
        Save(chat, "otto-ui-chat.png");

        // a reaction picture as Otto's reply (only when the addon is installed: run with OTTO_DATA pointing at one)
        if (Reactions.PathOf("laughing-cheers") is string pic)
        {
            chat.Clear();
            chat.AddMessage("my code finally compiled after 3 hours, react to that", user: true);
            chat.AddNote("Reacting with “laughing-cheers”", tool: true);
            chat.AddPicture(pic);
            chat.AddMessage("Three hours well spent. Enjoy it.", user: false);
            Hover(chat, new Point(-1, -1));
            Save(chat, "otto-ui-reaction.png");
        }

        var now = DateTime.Now;
        history.Show(new List<ChatStore.Summary>
        {
            new("a", "Find me the cheapest flights from Brisbane to Tokyo in April", "I found three options. The cheapest is Jetstar via Cairns at $612 return.", now.AddMinutes(-5)),
            new("b", "Tidy up my downloads folder", "Done. I moved 214 files into Documents, Pictures, Installers and Archives.", now.AddHours(-3),
                "Tidy up my downloads folder Done. I moved 214 files into Documents, Pictures, Installers and Archives. The two Japan itinerary PDFs went into Documents\\Travel."),
            new("c", "What's the weather tomorrow", "Brisbane tomorrow: 27 degrees, mostly sunny, 10% chance of rain.", now.AddDays(-1)),
            new("d", "Write a cover letter for the barista job using my resume", "Saved Cover letter.docx next to your resume. It's about 250 words.", now.AddDays(-3)),
            new("e", "i have this word doc and this onenote page open on zen. i want them merged", "", now.AddDays(-12)),
        }, "a");
        Hover(history, new Point(200, 195)); // over the second chat, to show its delete button
        Save(history, "otto-ui-history.png");

        // searching: "japan" is only inside the downloads chat, so its row shows the matching snippet
        history.Controls.OfType<CueTextBox>().Single().Text = "japan";
        Hover(history, new Point(-1, -1));
        Save(history, "otto-ui-history-search.png");
    }

    static void Hover(Control c, Point p) =>
        c.GetType().GetField("mouse", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c, p);

    static void Save(Control c, string name)
    {
        Thread.Sleep(500); // let fade-ins finish
        c.CreateControl();
        using var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
        bmp.Save(Path.Combine(Path.GetTempPath(), name));
    }
}

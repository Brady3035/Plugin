// Command genform builds Silo's native plugin settings form from the Jellyfin
// plugin's admin page, so the two keep the same settings, labels, help text
// and grouping. Run it from the Silo directory after refreshing
// web/configPage.html:
//
//	go run ./tools/genform
//
// It writes config_schema.json (the manifest's global_config_schema) and
// internal/api/form_gen.go (the field table the plugin reads values with).
// Settings that need the page's own controls (drag-to-order lists, library,
// collection and genre pickers) are left to the Moonbase page, and each
// User Defaults card says which.
package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"go/format"
	"os"
	"regexp"
	"slices"
	"strconv"
	"strings"

	"golang.org/x/net/html"

	"github.com/Moonfin-Client/Plugin/Silo/internal/pluginconfig"
)

type field struct {
	ID       string // element id on the page
	Key      string // configuration key the value lands in
	FormKey  string // key in Silo's form, Key unless several fields make one value
	Part     string // which part of a combined value this field is
	Defaults bool   // a DefaultUserSettings key rather than a top-level one
	Kind     string
	Fallback any

	Label, Desc, Placeholder string
	Element, InputType       string
	Options                  [][2]string // value, label
	Min, Max                 string
	Rows                     int
	Tab, PageSection         string
	Subtab, Group            string
	Card, Section            string
	Order                    int
	Seen                     bool
}

type card struct {
	key, title, desc string
	// notOnSilo folds the card's settings away, since they do nothing on
	// Silo.
	notOnSilo bool
}

// Cards in the order Silo shows them: what works on Silo first, then the
// features Silo can't run. The User Defaults cards follow, one per sub-tab of
// the page.
var cards = []card{
	{"moonbase", "Moonbase", "Settings sync and the server-wide rating keys. Messages, Apply Defaults, Broadcast and the settings that need drag and drop are on the Moonbase page, in the menu at the top right of this page.", false},
	{"seerr", "Seerr and Push Notifications", "Not available on Silo. Silo has its own requests feature, and the Seerr proxy and its push notifications aren't part of Moonbase on Silo. These settings are saved but do nothing.", true},
	{"lists", "Charts, Recommendations and Studio Logos", "Not available on Silo. These settings are saved but do nothing.", true},
	{"animeMarkers", "Anime Filler & Recap Markers", "Not available on Silo, because Silo doesn't let plugins read its library. These settings are saved but do nothing. The library picker is on the Moonbase page.", true},
	{"games", "Retro Games (EmulatorJS)", "Not available on Silo, because Silo doesn't let plugins read game files. These settings are saved but do nothing. The core upload and library picker are on the Moonbase page.", true},
	{"webClient", "Web Client", "Not available on Silo, because Moonbase doesn't host Moonfin web on Silo. These settings are saved but do nothing.", true},
}

const defaultsLead = "Defaults are the lowest-priority fallback: a user's device profile wins, then their global settings, then these. Not set means users keep their own choice."

// Which card and section each section of the page's other tabs goes to.
var pageSections = map[string][2]string{
	"":                             {"moonbase", "Settings Sync"},
	"MDBList Integration":          {"moonbase", "MDBList"},
	"TMDB Integration":             {"moonbase", "TMDB"},
	"Seerr Integration":            {"seerr", "Seerr"},
	"IMDb Charts":                  {"lists", "IMDb Charts"},
	"Recommendations":              {"lists", "Recommendations"},
	"Studio Logos":                 {"lists", "Studio Logos"},
	"Anime Filler & Recap Markers": {"animeMarkers", ""},
	"Retro Games (EmulatorJS)":     {"games", ""},
	"Connecting to this server":    {"webClient", ""},
}

var sectionNotes = map[string]string{
	"moonbase/MDBList":      "Ratings work on Silo. The official MDBList lists don't. Test key and Clear cache are on the Moonbase page.",
	"lists/Recommendations": "On Silo, Moonfin uses Silo's own recommendations.",
}

var (
	subtabTitles = map[string]string{}
	subtabOrder  []string
	pageOrder    int
)

// pageItem is something in a defaults sub-tab the form can't hold: a
// labeled control, or a whole group with no form fields (a builder).
type pageItem struct {
	group, text string
	isGroup     bool
}

// groupNames renames groups that only hold a builder, so the card's list
// reads on its own.
var groupNames = map[string]string{"Rows": "Home row layout"}

func main() {
	raw, err := os.ReadFile("web/configPage.html")
	check(err)
	page := string(raw)

	fields := mapFields(page)
	byID := map[string]*field{}
	for _, f := range fields {
		byID[f.ID] = f
	}
	doc, err := html.Parse(bytes.NewReader(raw))
	check(err)
	labels := map[string]string{}
	collectLabels(doc, labels)
	items := map[string][]pageItem{}
	var ctx context
	walk(doc, &ctx, byID, labels, items)
	fillScriptOptions(page, byID)
	pageOnly := pageOnlyLists(items, fields)

	var kept []*field
	for _, f := range fields {
		if !f.Seen || !place(f) {
			fmt.Fprintf(os.Stderr, "genform: skipping %s (%s), not on a form tab\n", f.Key, f.ID)
			continue
		}
		kept = append(kept, f)
	}
	slices.SortStableFunc(kept, func(a, b *field) int {
		if d := cardIndex(a.Card) - cardIndex(b.Card); d != 0 {
			return d
		}
		return a.Order - b.Order
	})

	schema := buildSchema(kept, page, pageOnly)
	out, err := json.MarshalIndent(schema, "", "  ")
	check(err)
	check(os.WriteFile("config_schema.json", append(out, '\n'), 0o644))
	check(os.WriteFile("internal/api/form_gen.go", goTable(kept), 0o644))
	fmt.Fprintf(os.Stderr, "genform: %d fields on %d cards\n", len(kept), len(schema))
}

// mapFields reads the page's save code, which pairs every element id with
// the configuration key it writes and how, plus the three settings the page
// builds from several plain controls.
func mapFields(page string) []*field {
	var out []*field
	add := func(id, key string, defaults bool, kind string, fallback any) {
		out = append(out, &field{ID: id, Key: key, FormKey: key, Defaults: defaults, Kind: kind, Fallback: fallback})
	}
	getters := map[string]string{
		"getNullableBoolSelect": "tribool", "getNullableRangeInput": "int",
		"getNullableRangeInputAsText": "intText", "getNullableIntInput": "int",
	}
	for _, m := range regexp.MustCompile(`config\.DefaultUserSettings\.(\w+)\s*=\s*(\w+)\('#(\w+)'\)`).FindAllStringSubmatch(page, -1) {
		if kind, ok := getters[m[2]]; ok {
			add(m[3], m[1], true, kind, nil)
		}
	}
	for _, m := range regexp.MustCompile(`config\.DefaultUserSettings\.(\w+)\s*=\s*document\.querySelector\('#(\w+)'\)\.value(?:\.trim\(\))?\s*\|\|\s*null`).FindAllStringSubmatch(page, -1) {
		add(m[2], m[1], true, "string", nil)
	}
	for _, m := range regexp.MustCompile(`config\.(\w+)\s*=\s*document\.querySelector\('#(\w+)'\)\.checked`).FindAllStringSubmatch(page, -1) {
		add(m[2], m[1], false, "bool", nil)
	}
	for _, m := range regexp.MustCompile(`config\.(\w+)\s*=\s*document\.querySelector\('#(\w+)'\)\.value(?:\.trim\(\))?\s*\|\|\s*(null|'[^']*')`).FindAllStringSubmatch(page, -1) {
		var fallback any
		if m[3] != "null" {
			fallback = strings.Trim(m[3], "'")
		}
		add(m[2], m[1], false, "string", fallback)
	}
	for _, m := range regexp.MustCompile(`config\.(\w+)\s*=\s*parseInt\(document\.querySelector\('#(\w+)'\)\.value,\s*10\)\s*\|\|\s*(\d+)`).FindAllStringSubmatch(page, -1) {
		n, _ := strconv.Atoi(m[3])
		add(m[2], m[1], false, "int", n)
	}

	// mediaSegmentActions: one select per segment type, joined as type:action.
	for _, t := range quotedList(scriptArray(page, "SEGMENT_TYPES")) {
		title := strings.ToUpper(t[:1]) + t[1:]
		out = append(out, &field{ID: "DefaultSegmentAction" + title, Key: "mediaSegmentActions",
			FormKey: "mediaSegmentAction" + title, Part: t, Defaults: true, Kind: "segment"})
	}
	// bottomNavbarTabs: one select per slot.
	for _, m := range regexp.MustCompile(`id="(DefaultBottomNavbarTab(\d+))"`).FindAllStringSubmatch(page, -1) {
		out = append(out, &field{ID: m[1], Key: "bottomNavbarTabs", FormKey: "bottomNavbarTab" + m[2],
			Part: m[2], Defaults: true, Kind: "slot"})
	}
	// seasonalRowHiddenHolidays: the page ticks the holidays that show.
	out = append(out, &field{ID: "DefaultSeasonalRowHolidays", Key: "seasonalRowHiddenHolidays",
		FormKey: "seasonalRowShownHolidays", Defaults: true, Kind: "shown"})
	return out
}

func scriptArray(page, name string) string {
	m := regexp.MustCompile(`var ` + name + ` = \[([\s\S]*?)\];`).FindStringSubmatch(page)
	if m == nil {
		check(fmt.Errorf("the page has no %s", name))
	}
	return m[1]
}

func quotedList(s string) []string {
	var out []string
	for _, m := range regexp.MustCompile(`'([^']*)'`).FindAllStringSubmatch(s, -1) {
		out = append(out, m[1])
	}
	return out
}

func scriptPairs(block string) [][3]string {
	var out [][3]string
	for _, m := range regexp.MustCompile(`\[\s*(?:'([^']*)'|"([^"]*)"),\s*(?:'([^']*)'|"([^"]*)")(?:,\s*(true))?\s*\]`).FindAllStringSubmatch(block, -1) {
		out = append(out, [3]string{m[1] + m[2], m[3] + m[4], m[5]})
	}
	return out
}

type context struct {
	tab, section, subtab, group string
}

func attr(n *html.Node, name string) string {
	for _, a := range n.Attr {
		if a.Key == name {
			return a.Val
		}
	}
	return ""
}

func hasClass(n *html.Node, class string) bool {
	return slices.Contains(strings.Fields(attr(n, "class")), class)
}

func text(n *html.Node) string {
	var b strings.Builder
	var rec func(*html.Node)
	rec = func(n *html.Node) {
		if n.Type == html.TextNode {
			b.WriteString(n.Data)
			b.WriteByte(' ')
		}
		if n.Type == html.ElementNode && (n.Data == "script" || n.Data == "style") {
			return
		}
		for c := n.FirstChild; c != nil; c = c.NextSibling {
			rec(c)
		}
	}
	rec(n)
	s := strings.Join(strings.Fields(b.String()), " ")
	for _, p := range []string{" .", " ,", " )", " :"} {
		s = strings.ReplaceAll(s, p, p[1:])
	}
	return siloText(strings.ReplaceAll(s, "( ", "("))
}

var (
	jellyfinVersion = regexp.MustCompile(`\s*\(Jellyfin \d+\+\)`)
	jellyfinPort    = regexp.MustCompile(`:8096\b`)
	jellyfinWeb     = regexp.MustCompile(`\bjellyfin-web\b`)
	jellyfinName    = regexp.MustCompile(`\bJellyfin\b`)
)

// siloText names the server Silo, as web/host.js does on the page.
func siloText(s string) string {
	s = jellyfinVersion.ReplaceAllString(s, "")
	s = strings.ReplaceAll(s, "jellyfin.example.com", "silo.example.com")
	s = jellyfinPort.ReplaceAllString(s, ":8090")
	s = jellyfinWeb.ReplaceAllString(s, "Silo's web app")
	return jellyfinName.ReplaceAllString(s, "Silo")
}

func collectLabels(n *html.Node, labels map[string]string) {
	if n.Type == html.ElementNode && n.Data == "label" {
		if id := attr(n, "for"); id != "" && labels[id] == "" {
			labels[id] = text(n)
		}
	}
	for c := n.FirstChild; c != nil; c = c.NextSibling {
		collectLabels(c, labels)
	}
}

// walk visits the page in order, tracking the tab, section, sub-tab and
// group each field sits under, and noting labels in the defaults tab that
// belong to settings the form can't hold.
func walk(n *html.Node, ctx *context, byID map[string]*field, labels map[string]string, items map[string][]pageItem) {
	saved := *ctx
	if n.Type == html.ElementNode {
		switch {
		case n.Data == "section" && hasClass(n, "moonfinTabPanel"):
			*ctx = context{tab: attr(n, "data-tab")}
		case n.Data == "h3" && hasClass(n, "sectionTitle"):
			ctx.section = text(n)
		case n.Data == "button" && hasClass(n, "defaultsSubtabBtn"):
			subtabTitles[attr(n, "data-subtab")] = text(n)
			subtabOrder = append(subtabOrder, text(n))
		case n.Data == "div" && hasClass(n, "defaultsSubtabPanel"):
			ctx.subtab, ctx.group = subtabTitles[attr(n, "data-subtab")], ""
		case n.Data == "h4" && ctx.subtab != "":
			ctx.group = text(n)
			items[ctx.subtab] = append(items[ctx.subtab], pageItem{group: ctx.group, text: ctx.group, isGroup: true})
		case n.Data == "label" && hasClass(n, "inputLabel") && ctx.subtab != "":
			if label := strings.TrimPrefix(text(n), "Default "); label != "" && !labelsField(n, byID) {
				items[ctx.subtab] = append(items[ctx.subtab], pageItem{group: ctx.group, text: label})
			}
		}
		if f, ok := byID[attr(n, "id")]; ok && !f.Seen {
			describe(f, n, ctx, labels)
		}
	}
	for c := n.FirstChild; c != nil; c = c.NextSibling {
		walk(c, ctx, byID, labels, items)
	}
	// A heading sets the context for the siblings after it, until the element
	// holding it ends.
	switch {
	case n.Type != html.ElementNode:
	case hasClass(n, "moonfinTabPanel"):
		*ctx = context{}
	case hasClass(n, "defaultsSubtabPanel"):
		ctx.subtab, ctx.group = saved.subtab, saved.group
	default:
		for c := n.FirstChild; c != nil; c = c.NextSibling {
			if c.Type == html.ElementNode && c.Data == "h4" {
				ctx.group = saved.group
			}
			if c.Type == html.ElementNode && c.Data == "h3" && hasClass(c, "sectionTitle") {
				ctx.section = saved.section
			}
		}
	}
}

// pageOnlyLists names, per sub-tab, what's left to the Moonbase page: the
// labeled controls the form can't hold, and groups with no form fields.
func pageOnlyLists(items map[string][]pageItem, fields []*field) map[string][]string {
	fieldsIn := map[string]int{}
	for _, f := range fields {
		if f.Seen {
			fieldsIn[f.Subtab+"/"+f.Group]++
		}
	}
	out := map[string][]string{}
	for subtab, list := range items {
		labeled := map[string]bool{}
		for _, it := range list {
			if !it.isGroup {
				labeled[it.group] = true
			}
		}
		for _, it := range list {
			switch {
			case it.isGroup && fieldsIn[subtab+"/"+it.group] == 0 && !labeled[it.group]:
				name := it.text
				if renamed := groupNames[name]; renamed != "" {
					name = renamed
				}
				out[subtab] = append(out[subtab], name)
			case !it.isGroup:
				out[subtab] = append(out[subtab], withGroup(it.group, it.text))
			}
		}
	}
	return out
}

// withGroup prefixes a label with its group when the label alone wouldn't
// say what it's for ("Libraries" in Media Bar).
func withGroup(group, label string) string {
	for _, g := range strings.Fields(strings.ToLower(group)) {
		for _, l := range strings.Fields(strings.ToLower(label)) {
			if len(g) >= 4 && len(l) >= 4 && g[:4] == l[:4] {
				return label
			}
		}
	}
	if group == "" {
		return label
	}
	return group + ": " + label
}

// labelsField reports whether a label belongs to a field the form holds:
// it names one, or sits in the same field container as one.
func labelsField(label *html.Node, byID map[string]*field) bool {
	if byID[attr(label, "for")] != nil {
		return true
	}
	for p := label.Parent; p != nil; p = p.Parent {
		if hasClass(p, "inputContainer") || hasClass(p, "selectContainer") || hasClass(p, "checkboxContainer") {
			return find(p, func(x *html.Node) bool { return byID[attr(x, "id")] != nil }) != nil
		}
	}
	return false
}

func describe(f *field, n *html.Node, ctx *context, labels map[string]string) {
	f.Seen = true
	pageOrder++
	f.Order = pageOrder
	f.Tab, f.PageSection, f.Subtab, f.Group = ctx.tab, ctx.section, ctx.subtab, ctx.group
	f.Element, f.InputType = n.Data, attr(n, "type")
	f.Placeholder = siloText(attr(n, "placeholder"))
	f.Min, f.Max = attr(n, "min"), attr(n, "max")
	f.Rows, _ = strconv.Atoi(attr(n, "rows"))
	if n.Data == "select" {
		for c := n.FirstChild; c != nil; c = c.NextSibling {
			if c.Type == html.ElementNode && c.Data == "option" {
				f.Options = append(f.Options, [2]string{attr(c, "value"), text(c)})
			}
		}
	}
	f.Label = labels[f.ID]
	if f.InputType == "checkbox" {
		for c := n.Parent.FirstChild; c != nil; c = c.NextSibling {
			if c.Type == html.ElementNode && c.Data == "span" {
				f.Label = text(c)
				// A status card's label also holds its on and off lines.
				if title := find(c, func(x *html.Node) bool { return strings.HasSuffix(attr(x, "class"), "Title") }); title != nil {
					f.Label = text(title)
				}
				break
			}
		}
	}
	for p := n.Parent; p != nil; p = p.Parent {
		if hasClass(p, "inputContainer") || hasClass(p, "checkboxContainer") || hasClass(p, "selectContainer") {
			if f.Label == "" {
				if l := find(p, func(x *html.Node) bool { return x.Data == "label" && hasClass(x, "inputLabel") }); l != nil {
					f.Label = text(l)
				}
			}
			if d := find(p, func(x *html.Node) bool { return x.Data == "div" && hasClass(x, "fieldDescription") }); d != nil {
				f.Desc = text(d)
			}
			break
		}
	}
	if f.Kind == "slot" {
		f.Label = "Bottom Bar Tab " + f.Part
		if f.Part != "1" {
			f.Desc = ""
		}
	}
	if f.Kind == "shown" {
		f.Label = "Holidays to show"
	}
	if f.Label == "" {
		f.Label = f.FormKey
	}
}

// place picks the card and section a field goes in, and reports false for a
// field outside the tabs the form covers.
func place(f *field) bool {
	if f.Tab == "defaults" {
		if f.Subtab == "" {
			return false
		}
		f.Card = defaultsCardKey(f.Subtab)
		f.Section = f.Group
		return true
	}
	where, ok := pageSections[f.PageSection]
	if !ok || (f.Tab != "settingsSync" && f.PageSection == "") {
		return false
	}
	f.Card, f.Section = where[0], where[1]
	if f.Card == "seerr" && (strings.HasPrefix(f.Key, "Push") || strings.HasPrefix(f.Key, "Fcm")) {
		f.Section = "Push Notifications"
	}
	return true
}

func defaultsCardKey(subtab string) string {
	return "defaults" + strings.ReplaceAll(subtab, " ", "")
}

// fillScriptOptions gives the selects the page fills from script the same
// options it gives them.
func fillScriptOptions(page string, byID map[string]*field) {
	languages, sorts := scriptPairs(scriptArray(page, "LANGUAGE_OPTIONS")), scriptPairs(scriptArray(page, "LIBRARY_SORT_OPTIONS"))
	for _, m := range regexp.MustCompile(`(fillLanguageSelect|fillSortSelect)\('#(\w+)'(?:,\s*(true|false))?\)`).FindAllStringSubmatch(page, -1) {
		f := byID[m[2]]
		if f == nil || len(f.Options) > 0 {
			continue
		}
		f.Options = [][2]string{{"", "Not set (user decides)"}}
		if m[1] == "fillLanguageSelect" {
			for _, l := range languages {
				if l[0] == "auto" && m[3] == "true" {
					continue
				}
				label := l[1]
				if l[0] != "auto" {
					label += " (" + l[0] + ")"
				}
				f.Options = append(f.Options, [2]string{l[0], label})
			}
			continue
		}
		for _, o := range sorts {
			if o[2] == "" || m[3] == "true" {
				f.Options = append(f.Options, [2]string{o[0], o[1]})
			}
		}
	}
	actions := scriptPairs(scriptArray(page, "SEGMENT_ACTIONS"))
	tabs := scriptPairs(scriptArray(page, "BOTTOM_NAVBAR_TABS"))
	holidays := scriptPairs(scriptArray(page, "SEASONAL_HOLIDAYS"))
	for _, f := range byID {
		switch f.Kind {
		case "segment":
			f.Options = [][2]string{{"", "Not set (user decides)"}}
			for _, a := range actions {
				f.Options = append(f.Options, [2]string{a[0], a[1]})
			}
		case "slot":
			blank := "None"
			if f.Part == "1" {
				blank = "Not set (automatic)"
			}
			f.Options = [][2]string{{"", blank}}
			for _, t := range tabs {
				f.Options = append(f.Options, [2]string{t[0], t[1]})
			}
		case "shown":
			f.Options = nil
			for _, h := range holidays {
				f.Options = append(f.Options, [2]string{h[0], h[1]})
			}
		}
	}
}

func find(n *html.Node, match func(*html.Node) bool) *html.Node {
	for c := n.FirstChild; c != nil; c = c.NextSibling {
		if c.Type == html.ElementNode && match(c) {
			return c
		}
		if found := find(c, match); found != nil {
			return found
		}
	}
	return nil
}

func cardIndex(key string) int {
	if i := slices.IndexFunc(cards, func(c card) bool { return c.key == key }); i >= 0 {
		return i
	}
	if i := slices.IndexFunc(subtabOrder, func(t string) bool { return defaultsCardKey(t) == key }); i >= 0 {
		return len(cards) + i
	}
	return 1 << 20
}

type rule struct {
	parents []string
	off     []string
	fields  []string
}

func dependencyRules(page string) []rule {
	var out []rule
	re := regexp.MustCompile(`\{ parent: ('[^']*'|\[[^\]]*\]),(?: off: (\[[^\]]*\]),)? fields: (\[[^\]]*\]) \}`)
	for _, m := range re.FindAllStringSubmatch(page, -1) {
		r := rule{parents: quotedList(m[1]), off: quotedList(m[2]), fields: quotedList(m[3])}
		if len(r.off) == 0 {
			r.off = []string{"false"}
		}
		out = append(out, r)
	}
	return out
}

func buildSchema(fields []*field, page string, pageOnly map[string][]string) []map[string]any {
	defaults := pluginconfig.Defaults()
	byID := map[string]*field{}
	for _, f := range fields {
		byID[f.ID] = f
	}
	// show_when for fields whose page rules each have one parent on the same
	// card. Silo shows a field only when all of its conditions hold, so a field
	// also takes its parents' conditions and hides when they do.
	type condition struct {
		parent string
		when   map[string]any
	}
	own := map[string][]condition{}
	for _, r := range dependencyRules(page) {
		if len(r.parents) != 1 {
			continue
		}
		parent := byID[r.parents[0]]
		if parent == nil || parent.Kind == "shown" {
			continue
		}
		var values []string
		switch {
		case parent.Kind == "bool":
			values = []string{"true", "false"}
		case len(parent.Options) > 0:
			for _, o := range parent.Options {
				values = append(values, o[0])
			}
		default:
			continue
		}
		c := condition{parent.ID, map[string]any{"field": parent.FormKey,
			"equals": slices.DeleteFunc(values, func(v string) bool { return slices.Contains(r.off, v) })}}
		for _, id := range r.fields {
			f := byID[id]
			if f == nil || f.Card != parent.Card {
				continue
			}
			if f.Kind != "slot" {
				own[f.ID] = append(own[f.ID], c)
				continue
			}
			// The page hides all of the bottom bar tab slots with the first.
			for _, g := range fields {
				if g.Kind == "slot" {
					own[g.ID] = append(own[g.ID], c)
				}
			}
		}
	}
	var showWhen func(id string, seen map[string]bool) []map[string]any
	showWhen = func(id string, seen map[string]bool) []map[string]any {
		if seen[id] {
			return nil
		}
		seen[id] = true
		var out []map[string]any
		add := func(w map[string]any) {
			if !slices.ContainsFunc(out, func(o map[string]any) bool { return o["field"] == w["field"] }) {
				out = append(out, w)
			}
		}
		for _, c := range own[id] {
			add(c.when)
			for _, w := range showWhen(c.parent, seen) {
				add(w)
			}
		}
		return out
	}

	order := slices.Clone(cards)
	for _, t := range subtabOrder {
		order = append(order, card{key: defaultsCardKey(t), title: "User Defaults: " + t})
	}
	var out []map[string]any
	firstDefaults := true
	for _, c := range order {
		props := map[string]any{}
		var formFields []map[string]any
		var sections []map[string]any
		sectionAt := map[string]int{}
		subtab := ""
		for _, f := range fields {
			if f.Card != c.key {
				continue
			}
			subtab = f.Subtab
			prop, ff := control(f, defaults)
			props[f.FormKey] = prop
			if w := showWhen(f.ID, map[string]bool{}); len(w) > 0 {
				ff["show_when"] = w
			}
			formFields = append(formFields, ff)
			if f.Section == "" {
				continue
			}
			i, ok := sectionAt[f.Section]
			if !ok {
				i = len(sections)
				sectionAt[f.Section] = i
				s := map[string]any{"key": fmt.Sprintf("s%d", i+1), "title": f.Section, "field_keys": []string{}}
				if note := sectionNotes[c.key+"/"+f.Section]; note != "" {
					s["description"] = note
				}
				sections = append(sections, s)
			}
			sections[i]["field_keys"] = append(sections[i]["field_keys"].([]string), f.FormKey)
		}
		if len(formFields) == 0 {
			continue
		}
		desc := c.desc
		if subtab != "" {
			var parts []string
			if firstDefaults {
				parts = append(parts, defaultsLead)
				firstDefaults = false
			}
			if items := pageOnly[subtab]; len(items) > 0 {
				parts = append(parts, "On the Moonbase page, in the menu at the top right of this page: "+joinList(items)+".")
			}
			desc = strings.Join(parts, " ")
		}
		// A lone section says nothing the card title doesn't. Long cards fold
		// their sections so the page stays scannable.
		if len(sections) == 1 && sections[0]["description"] == nil {
			sections = nil
		}
		if c.notOnSilo && len(sections) == 0 {
			var keys []string
			for _, ff := range formFields {
				keys = append(keys, ff["key"].(string))
			}
			sections = []map[string]any{{"key": "s1", "title": "Settings", "field_keys": keys}}
		}
		if len(formFields) > 12 || c.notOnSilo {
			for _, s := range sections {
				s["collapsible"], s["collapsed_default"] = true, true
			}
		}
		jsonSchema, _ := json.Marshal(map[string]any{"type": "object", "properties": props})
		form := map[string]any{"submit_label": "Save", "fields": formFields}
		if len(sections) > 0 {
			form["sections"] = sections
		}
		entry := map[string]any{"key": c.key, "title": c.title, "json_schema": string(jsonSchema), "admin_form": form}
		if desc != "" {
			entry["description"] = desc
		}
		out = append(out, entry)
	}
	return out
}

func joinList(items []string) string {
	items = slices.Compact(slices.Clone(items))
	switch len(items) {
	case 1:
		return items[0]
	case 2:
		return items[0] + " and " + items[1]
	}
	return strings.Join(items[:len(items)-1], ", ") + " and " + items[len(items)-1]
}

func control(f *field, defaults map[string]any) (map[string]any, map[string]any) {
	ff := map[string]any{"key": f.FormKey, "label": f.Label}
	if f.Desc != "" {
		ff["description"] = f.Desc
	}
	if f.Placeholder != "" {
		ff["placeholder"] = f.Placeholder
	}
	var prop map[string]any
	options := func() {
		var opts []map[string]any
		for _, o := range f.Options {
			opts = append(opts, map[string]any{"value": o[0], "label": o[1]})
		}
		ff["options"] = opts
	}
	switch {
	case f.Kind == "bool":
		prop = map[string]any{"type": "boolean"}
		ff["control"] = "ADMIN_FORM_CONTROL_SWITCH"
	case f.Kind == "shown":
		prop = map[string]any{"type": "array", "items": map[string]any{"type": "string"}}
		ff["control"] = "ADMIN_FORM_CONTROL_MULTI_SELECT"
		options()
		var all []any
		for _, o := range f.Options {
			all = append(all, o[0])
		}
		ff["default_value"] = all
	case (f.Kind == "int" || f.Kind == "intText") && f.Element == "select":
		prop = map[string]any{"type": "string"}
		ff["control"] = "ADMIN_FORM_CONTROL_SELECT"
		options()
	case f.Kind == "int" || f.Kind == "intText":
		prop = map[string]any{"type": "integer"}
		ff["control"] = "ADMIN_FORM_CONTROL_NUMBER"
		v := map[string]any{}
		if n, err := strconv.ParseFloat(f.Min, 64); err == nil {
			v["has_min"], v["min"] = true, n
		}
		if n, err := strconv.ParseFloat(f.Max, 64); err == nil {
			v["has_max"], v["max"] = true, n
		}
		if len(v) > 0 {
			ff["validation"] = v
		}
		if f.Defaults && f.Placeholder == "" {
			ff["placeholder"] = "Not set (user decides)"
		}
	case f.Element == "select" || len(f.Options) > 0:
		prop = map[string]any{"type": "string"}
		ff["control"] = "ADMIN_FORM_CONTROL_SELECT"
		options()
	case f.Element == "textarea":
		prop = map[string]any{"type": "string"}
		ff["control"] = "ADMIN_FORM_CONTROL_TEXTAREA"
		if f.Rows > 0 {
			ff["rows"] = f.Rows
		}
	default:
		prop = map[string]any{"type": "string"}
		ff["control"] = "ADMIN_FORM_CONTROL_TEXT"
	}
	if isSecret(f) {
		ff["secret"] = true
		if f.Element != "textarea" {
			ff["control"] = "ADMIN_FORM_CONTROL_PASSWORD"
		}
	}
	if !f.Defaults {
		if v, ok := defaults[f.Key]; ok {
			ff["default_value"] = v
		} else if f.Fallback != nil {
			ff["default_value"] = f.Fallback
		}
	}
	return prop, ff
}

func isSecret(f *field) bool {
	return f.InputType == "password" || strings.HasSuffix(f.Key, "ApiKey") || strings.HasSuffix(f.Key, "AppKey") || f.Key == "FcmServiceAccountJson"
}

func goTable(fields []*field) []byte {
	var b bytes.Buffer
	b.WriteString("// Code generated by go run ./tools/genform. DO NOT EDIT.\n\npackage api\n\n")
	b.WriteString("// formFields are the settings on Silo's native form, from web/configPage.html.\nvar formFields = []formField{\n")
	for _, f := range fields {
		kind := f.Kind
		if kind == "int" && f.Element == "select" {
			kind = "intChoice"
		}
		fmt.Fprintf(&b, "\t{Entry: %q, Field: %q, Key: %q, Defaults: %t, Kind: kind%s", f.Card, f.FormKey, f.Key, f.Defaults, strings.ToUpper(kind[:1])+kind[1:])
		if f.Part != "" {
			fmt.Fprintf(&b, ", Part: %q", f.Part)
		}
		if f.Kind == "shown" {
			var all []string
			for _, o := range f.Options {
				all = append(all, strconv.Quote(o[0]))
			}
			fmt.Fprintf(&b, ", Options: []string{%s}", strings.Join(all, ", "))
		}
		b.WriteString("},\n")
	}
	b.WriteString("}\n")
	src, err := format.Source(b.Bytes())
	check(err)
	return src
}

func check(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, "genform:", err)
		os.Exit(1)
	}
}

using System.Text.Json.Serialization;

namespace AnnasArchive.API.Reader2.Story;

/*
 * Every enum below is serialised by name, not by number.
 *
 * The application registers no global string-enum converter, so the default is
 * an integer — and the cast list compares a tier against "Major". It matched
 * nothing, for anybody, from the day it shipped: the table opened on its default
 * filter, found no one, and reported "27 not shown" beside "Nothing matches
 * those filters", which is exactly what it should say when a filter genuinely
 * excludes everybody. The bug hid itself, because the row that would have thrown
 * on `tier.toLowerCase()` was never rendered.
 *
 * The converters are put here rather than on the whole application: Reader I's
 * endpoints have their own clients and their own expectations, and changing the
 * wire format under all of them to fix one table would be a far larger claim
 * than this is. Reading still accepts a number, so models stored under the old
 * shape load unchanged.
 */

/// <summary>
/// How much of the story somebody is. Ordered by importance so that "promotes
/// freely, demotes slowly" is a comparison rather than a table.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActorTier { Mentioned = 0, Minor = 1, Secondary = 2, Major = 3 }

/// <summary>What kind of thing a group is. The same set serves both story lenses.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroupKind { Family, Household, MilitaryUnit, SocialCircle, PoliticalFaction, Other }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThreadStatus { Active, Dormant, Resolved, Abandoned }

/// <summary>
/// What kind of place something is. One set for both story lenses, as with
/// <see cref="GroupKind"/> — a novel's tavern and a campaign's forward base are
/// the same shape of thing to a reader trying to remember where they were.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaceKind { Settlement, Building, Region, Vessel, Realm, Other }

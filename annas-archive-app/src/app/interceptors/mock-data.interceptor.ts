import { HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { of, delay } from 'rxjs';

const isLocalDev = () => window.location.hostname === 'localhost';

const MOCK_EPUBS = [
  {
    path: '/Dropbox/Books/Foucaults_Pendulum.epub',
    name: "Foucault's Pendulum.epub",
    size: 2950000,
    serverModified: new Date(2024, 0, 15).toISOString()
  },
  {
    path: '/Dropbox/Books/The_Name_of_the_Rose.epub',
    name: 'The Name of the Rose.epub',
    size: 1850000,
    serverModified: new Date(2024, 1, 20).toISOString()
  }
];

const MOCK_READER_BOOKS = MOCK_EPUBS.map((book, idx) => ({
  fileName: book.name,
  readerKey: book.path,
  title: book.name.replace(/\.epub$/i, ''),
  authors: ['Unknown Author'],
  format: 'EPUB',
  coverUrl: null,
  hasSummaries: idx === 0
}));

const MOCK_CHAPTERS = [
  { id: 0, title: 'Chapter 1: Keter', level: 0, wordCount: 8542 },
  { id: 1, title: 'Chapter 2: Hokhmah', level: 0, wordCount: 7234 },
  { id: 2, title: 'Chapter 3: Binah', level: 0, wordCount: 9123 },
  { id: 3, title: 'Chapter 4: Hesed', level: 0, wordCount: 6891 }
];

const MOCK_CHAPTER_CONTENT = `We have divers curious Clocks; And other like Motions of Return.... Wee have also Houses of the Senses, where we represent all manner of Feats of Juggling, False Apparitions, Impostures, and Illusions.... These are (my sonne) the Riches of Salomon's House.

—Francis Bacon, The New Atlantis, ed. Rawley, London, 1627, pp. 41-42

I gained control of my nerves, my imagination. I had to play this ironically, as I had been playing it until a few days before, not letting myself become involved. I was in a museum and had to be dramatically clever and clearheaded.

I looked at the now-familiar planes above me: I could climb into the fuselage of a biplane, to await the night as if I were flying over the Channel, anticipating the Legion of Honor. The names of the automobiles on the ground had an affectionately nostalgic ring. The 1932 Hispano-Suiza was handsome, welcoming, but too close to the front desk. I might have slipped past the attendant if I had turned up in plus fours and Norfolk jacket, stepping aside for a lady in a cream-colored suit, with a long scarf wound around her slender neck, a cloche pulled over her bobbed hair. The 1931 Citroën C64 was shown only in cross section, an excellent educational display but a ridiculous hiding place. Cugnot's enormous steam automobile, all boiler, or cauldron, was out of the question. I looked to the right, where velocipedes with huge art-nouveau wheels and draisiennes with their flat, scooterlike bars evoked gentlemen in stovepipe hats, knights of progress pedaling through the Bois de Boulogne.

Across from the velocipedes were cars with bodies intact, ample receptacles. Perhaps not the 1945 Panhard Dyna-X, too open and too near the entrance, and forget the elegant barouche landau, too visible even in the shadows. But there was a Peugeot Quadricycle of 1898, all chassis, engine, handlebars, and spokes, and behind it, in two rows, some proud limousines: a Renault from the fifties; a sixties Mercedes-Benz; a veteran Rolls; and a nineteen-thirties Peugeot 402 Eclipse, with a retractable roof. They stood side by side like the coaches of a first-class express train permanently at rest.

The longer I stood among the exhibits, the more I felt as if the past were breathing over my shoulder. I imagined passengers boarding these vehicles, hats tilted against the rain, gloves clasped around leather suitcases. Somewhere a clock chimed, perhaps one of those curious clocks mentioned earlier, marking not the passage of time but the weight of memory itself. Each machine seemed to insist on its own story, a chain of causes and effects, of hopes and missteps, that led to this frozen display.

I walked on, and the scene shifted to maritime relics: models of clippers and steamships, sextants and compasses, maps yellowed with the salt of imagined voyages. The curator's notes described daring crossings and disastrous shipwrecks, lists of cargoes and crews, and distant ports glimpsed by lantern light. It was impossible not to feel the tug of the unknown, as if every ship were still poised to weigh anchor and vanish into fog.

In a quiet corner I found scientific instruments—telescopes, astrolabes, and dusty microscopes. Their brass fittings glowed faintly, promising revelations about worlds both vast and infinitesimal. A plaque recalled Bacon's call for a "new Atlantis" founded on experiment and observation. The juxtaposition with the automobiles and clocks struck me as deliberate: a testament to humankind's hunger to measure, to travel, to control, yet always to dream of something beyond our grasp.

Further along, a reconstructed study displayed shelves of obscure volumes, quills resting in inkwells, and notes pinned to a corkboard in frantic clusters. It evoked the fevered efforts of scholars and inventors, men and women convinced that meaning lay just one more connection away. I felt a kinship with them, and a warning: the desire to see patterns could illuminate, or consume.

Finally, as the lights dimmed for closing, I returned to the hall of clocks. Their hands ticked in complex rhythms, some racing, some lagging, as if time itself were elastic. I realized the museum was a map of human curiosity, charting routes through the senses and the mind. I left knowing I would return, if only to listen again to the cadence of those impossible machines.`;

const MOCK_SUMMARY = `In this passage from "Foucault's Pendulum," the narrator navigates a museum filled with historical artifacts, reflecting on their significance while contemplating a complex historical and philosophical exploration. The juxtaposition of Francis Bacon's "The New Atlantis" highlights themes of curiosity, illusion, and the interplay between reality and deception. The narrator's ironic detachment contrasts with the nostalgia evoked by the vintage automobiles, emphasizing a longing for a past that is both alluring and unsettling. Literary techniques such as vivid imagery and detailed descriptions create a rich tapestry of the museum's environment, while the internal monologue reveals the character's psychological state, caught between the desire for adventure and the constraints of reality. The passage underscores the theme of exploration, both intellectual and physical, as the narrator grapples with the tension between appearance and truth.

Definitions:
- **Divers**: Various or several different types
- **Draisiennes**: Early form of bicycle without pedals, propelled by pushing feet against ground
- **Fuselage**: Main body of an aircraft
- **Barouche**: Four-wheeled horse-drawn carriage with collapsible hood
- **Ironically**: In a way that uses irony or detachment to cope with a situation`;

const MOCK_FLASHCARDS = [
  {
    term: 'Pendulum',
    definition: 'A weight suspended from a pivot so it can swing freely',
    etymology: 'From Latin pendulus "hanging down"',
    usageExamples: [
      'Foucault used a pendulum to demonstrate Earth\'s rotation',
      'The pendulum swung back and forth with perfect regularity'
    ],
    notes: 'Central metaphor in Umberto Eco\'s novel'
  },
  {
    term: 'Kabbalah',
    definition: 'Ancient Jewish mystical tradition',
    etymology: 'From Hebrew qabbālāh "tradition, received lore"',
    usageExamples: [
      'The novel explores Kabbalistic symbolism throughout',
      'Medieval Kabbalah influenced many esoteric traditions'
    ],
    notes: 'Key theme in understanding the novel\'s structure'
  }
];

const MOCK_LEARN_MORE = `<p>The <strong>pendulum</strong> has been a subject of scientific and philosophical fascination for centuries. Léon Foucault's famous pendulum experiment in 1851 provided the first direct evidence of Earth's rotation.</p>

<p>In Umberto Eco's novel "Foucault's Pendulum," the pendulum becomes a central metaphor for:</p>
<ul>
  <li>The search for hidden patterns in history</li>
  <li>The danger of conspiracy thinking</li>
  <li>The interplay between order and chaos</li>
</ul>

<p>For further reading, you can explore the following links:</p>
<a href="https://en.wikipedia.org/wiki/Foucault_pendulum">Wikipedia - Foucault Pendulum</a>
<a href="https://en.wikipedia.org/wiki/Umberto_Eco">Wikipedia - Umberto Eco</a>`;

const MOCK_WIKI_IMAGES = [
  'https://upload.wikimedia.org/wikipedia/commons/thumb/e/e3/Foucault_pendulum_animated.gif/220px-Foucault_pendulum_animated.gif',
  'https://upload.wikimedia.org/wikipedia/commons/thumb/6/6b/Pendule_de_Foucault.jpg/440px-Pendule_de_Foucault.jpg'
];

export const mockDataInterceptor: HttpInterceptorFn = (req, next) => {
  // Only intercept in local dev mode
  if (!isLocalDev()) {
    return next(req);
  }

  const url = req.url;
  console.log('🎭 Mock Interceptor:', req.method, url);

  // Mock Dropbox EPUBs list
  if (url.includes('/dropbox/epubs') && req.method === 'GET') {
    return of(new HttpResponse({ status: 200, body: MOCK_EPUBS })).pipe(delay(300));
  }

  // Mock library reader list
  if (url.includes('/api/library/reader/books') && req.method === 'GET') {
    return of(new HttpResponse({ status: 200, body: MOCK_READER_BOOKS })).pipe(delay(300));
  }

  // Mock EPUB chapters
  if (url.includes('/dropbox/epub/chapters') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: { chapters: MOCK_CHAPTERS }
    })).pipe(delay(200));
  }

  if (url.includes('/api/library/reader/epub/chapters') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: { chapters: MOCK_CHAPTERS }
    })).pipe(delay(200));
  }

  // Mock chapter content
  if (url.includes('/dropbox/epub/chapter') && req.method === 'GET') {
    const wordCount = MOCK_CHAPTER_CONTENT.match(/\S+/g)?.length ?? MOCK_CHAPTER_CONTENT.length;
    return of(new HttpResponse({
      status: 200,
      body: {
        content: MOCK_CHAPTER_CONTENT,
        wordCount
      }
    })).pipe(delay(400));
  }

  if (url.includes('/api/library/reader/epub/chapter') && req.method === 'GET') {
    const wordCount = MOCK_CHAPTER_CONTENT.match(/\S+/g)?.length ?? MOCK_CHAPTER_CONTENT.length;
    return of(new HttpResponse({
      status: 200,
      body: {
        content: MOCK_CHAPTER_CONTENT,
        wordCount
      }
    })).pipe(delay(400));
  }

  // Mock EPUB status
  if (url.includes('/dropbox/epub/status') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: {
        percent: 100,
        cachedAt: new Date().toISOString(),
        chaptersCached: 4,
        chaptersTotal: 4,
        inProgress: false
      }
    })).pipe(delay(150));
  }

  if (url.includes('/api/library/reader/epub/status') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: {
        percent: 100,
        cachedAt: new Date().toISOString(),
        chaptersCached: 4,
        chaptersTotal: 4,
        inProgress: false
      }
    })).pipe(delay(150));
  }

  // Mock start indexing
  if (url.includes('/dropbox/epub/index') && req.method === 'POST') {
    return of(new HttpResponse({
      status: 200,
      body: { started: true }
    })).pipe(delay(100));
  }

  if (url.includes('/api/library/reader/epub/index') && req.method === 'POST') {
    return of(new HttpResponse({
      status: 200,
      body: { started: true }
    })).pipe(delay(100));
  }

  // Mock delete index
  if (url.includes('/dropbox/epub/index') && req.method === 'DELETE') {
    return of(new HttpResponse({
      status: 200,
      body: { removed: true }
    })).pipe(delay(100));
  }

  if (url.includes('/api/library/reader/epub/index') && req.method === 'DELETE') {
    return of(new HttpResponse({
      status: 200,
      body: { removed: true }
    })).pipe(delay(100));
  }

  // Mock book search
  if (url.includes('/dropbox/epub/search') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: [
        {
          chapterId: 1,
          title: 'Chapter 2: Hokhmah',
          matchCount: 3,
          snippet: '...curious Clocks and Motions of Return...'
        }
      ]
    })).pipe(delay(500));
  }

  if (url.includes('/api/library/reader/epub/search') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: [
        {
          chapterId: 1,
          title: 'Chapter 2: Hokhmah',
          matchCount: 3,
          snippet: '...curious Clocks and Motions of Return...'
        }
      ]
    })).pipe(delay(500));
  }

  // Mock summarize
  if (url.includes('/api/ai/summarize') && req.method === 'POST') {
    return of(new HttpResponse({
      status: 200,
      body: { summary: MOCK_SUMMARY }
    })).pipe(delay(1500));
  }

  // Mock learn more
  if (url.includes('/api/ai/vocab/learn-more') && req.method === 'POST') {
    return of(new HttpResponse({
      status: 200,
      body: { detail: MOCK_LEARN_MORE }
    })).pipe(delay(1000));
  }

  // Mock wiki images
  if (url.includes('/api/media/wiki-images') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: { images: MOCK_WIKI_IMAGES }
    })).pipe(delay(600));
  }

  // Mock flashcards - get
  if (url.includes('/api/ai/flashcards') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: MOCK_FLASHCARDS
    })).pipe(delay(300));
  }

  // Mock flashcards - create
  if (url.includes('/api/ai/flashcards') && req.method === 'POST') {
    const body = req.body as any;
    return of(new HttpResponse({
      status: 200,
      body: {
        term: body.term,
        definition: body.definition,
        etymology: `Etymology for ${body.term}`,
        usageExamples: [`Example usage of ${body.term}`],
        notes: 'Generated flashcard'
      }
    })).pipe(delay(1200));
  }

  // Mock flashcards - clear
  if (url.includes('/api/ai/flashcards') && req.method === 'DELETE') {
    return of(new HttpResponse({
      status: 200,
      body: { cleared: true }
    })).pipe(delay(200));
  }

  // Mock gaming PC status
  if (url.includes('/api/gaming/status') && req.method === 'GET') {
    return of(new HttpResponse({
      status: 200,
      body: {
        isOnline: false,
        ipAddress: '192.168.0.80',
        lastChecked: new Date().toISOString()
      }
    })).pipe(delay(400));
  }

  // Mock gaming PC toggle
  if (url.includes('/api/gaming/toggle') && req.method === 'POST') {
    return of(new HttpResponse({
      status: 200,
      body: {
        success: true,
        action: 'wake',
        message: 'Mock operation successful',
        output: 'Mock server output\nOperation completed successfully'
      }
    })).pipe(delay(2000));
  }

  // Pass through for everything else
  return next(req);
};

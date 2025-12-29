import { Component, Inject, OnInit, AfterViewInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AnnaArchiveApiService } from '../services/anna-archive-api.service';
import { CharacterGraphResponse } from '../models/dropbox-epub.model';

declare var anychart: any;

export interface CharacterGraphModalData {
  dropboxPath: string;
  bookTitle: string;
}

@Component({
  selector: 'app-character-graph-modal',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './character-graph-modal.component.html',
  styleUrls: ['./character-graph-modal.component.scss']
})
export class CharacterGraphModalComponent implements OnInit, AfterViewInit, OnDestroy {
  loading = true;
  error: string | null = null;
  graphData: CharacterGraphResponse | null = null;
  private chart: any;
  selectedNodeId: string | null = null;
  selectedEdge: { from: string; to: string } | null = null;
  detailsVisible = false;

  constructor(
    public dialogRef: MatDialogRef<CharacterGraphModalComponent>,
    @Inject(MAT_DIALOG_DATA) public data: CharacterGraphModalData,
    private api: AnnaArchiveApiService
  ) {}

  ngOnInit(): void {
    this.loadGraph();
  }

  ngAfterViewInit(): void {
    // Chart will be created after data loads
  }

  ngOnDestroy(): void {
    if (this.chart) {
      this.chart.dispose();
    }
  }

  private loadGraph(): void {
    this.loading = true;
    this.error = null;

    // Try to load existing graph first
    this.api.getCharacterGraph(this.data.dropboxPath).subscribe({
      next: response => {
        // Check if graph needs updating
        if (response.needsUpdate) {
          console.log(`📊 Graph is stale (${response.summaryCount} → ${response.currentSummaryCount} summaries). Regenerating...`);
          this.generateGraph();
        } else {
          console.log(`✅ Using cached graph (${response.summaryCount} summaries)`);
          this.graphData = response;
          this.loading = false;
          setTimeout(() => this.renderGraph(), 100);
        }
      },
      error: () => {
        // No graph exists, generate a new one
        this.generateGraph();
      }
    });
  }

  generateGraph(): void {
    this.loading = true;
    this.error = null;

    console.log('📊 Generating character graph for', this.data.bookTitle);

    this.api.generateCharacterGraph({
      dropboxPath: this.data.dropboxPath,
      bookTitle: this.data.bookTitle,
      context: `A novel titled "${this.data.bookTitle}"`
    }).subscribe({
      next: graph => {
        this.graphData = graph;
        this.loading = false;
        setTimeout(() => this.renderGraph(), 100);
        console.log('✅ Character graph generated:', graph);
      },
      error: err => {
        console.error('Failed to generate character graph', err);
        this.error = 'Failed to generate character graph. Please try again.';
        this.loading = false;
      }
    });
  }

  private formatCharacterName(name: string): string {
    // Common military and honorific titles
    const titles: { [key: string]: string } = {
      'cpt': 'Cpt.',
      'captain': 'Cpt.',
      'lt': 'Lt.',
      'lieutenant': 'Lt.',
      'mag': 'Mag.',
      'magistrate': 'Mag.',
      'col': 'Col.',
      'colonel': 'Col.',
      'gen': 'Gen.',
      'general': 'Gen.',
      'adm': 'Adm.',
      'admiral': 'Adm.',
      'dr': 'Dr.',
      'doctor': 'Dr.',
      'prof': 'Prof.',
      'professor': 'Prof.',
      'sgt': 'Sgt.',
      'sergeant': 'Sgt.',
      'maj': 'Maj.',
      'major': 'Maj.',
      'cmdr': 'Cmdr.',
      'commander': 'Cmdr.'
    };

    // Split the name into words
    const words = name.trim().split(/\s+/);

    return words.map((word, index) => {
      const lowerWord = word.toLowerCase();

      // Check if it's a title
      if (titles[lowerWord]) {
        return titles[lowerWord];
      }

      // Check for compound titles like "lt col" or "lt. col."
      if (index < words.length - 1) {
        const compoundTitle = `${lowerWord} ${words[index + 1].toLowerCase()}`.replace(/\./g, '');
        if (compoundTitle === 'lt col' || compoundTitle === 'lieutenant colonel') {
          words[index + 1] = ''; // Skip next word
          return 'Lt. Col.';
        }
      }

      // Capitalize first letter of each word
      return word.charAt(0).toUpperCase() + word.slice(1).toLowerCase();
    }).filter(w => w !== '').join(' ');
  }

  private renderGraph(): void {
    if (!this.graphData || !this.graphData.nodes.length) {
      this.error = 'No character data available';
      return;
    }

    const container = document.getElementById('character-graph-container');
    if (!container) {
      console.error('Graph container not found');
      return;
    }

    // Calculate connection count for each node to determine size
    const connectionCount = new Map<string, number>();
    this.graphData.edges.forEach(edge => {
      connectionCount.set(edge.from, (connectionCount.get(edge.from) || 0) + 1);
      connectionCount.set(edge.to, (connectionCount.get(edge.to) || 0) + 1);
    });

    // Prepare data for AnyChart with dynamic sizing
    const nodes: any[] = this.graphData.nodes.map(node => {
      const connections = connectionCount.get(node.id) || 0;
      // Base size of 20, plus 8 per connection (capped at 80)
      const nodeSize = Math.min(20 + connections * 8, 80);
      const formattedLabel = this.formatCharacterName(node.label);

      return {
        id: node.id,
        x: Math.random() * 400,
        y: Math.random() * 400,
        height: nodeSize,
        width: nodeSize,
        label: {
          enabled: true,
          text: formattedLabel,
          fontSize: 12,
          fontWeight: 'bold'
        },
        tooltip: {
          useHtml: true,
          format: () => {
            const detailedDesc = node.detailedDescription || node.description;
            return `<div style="padding: 8px; max-width: 300px;">
              <strong style="font-size: 14px;">${formattedLabel}</strong><br/>
              <span style="font-size: 12px; color: #666;">${detailedDesc}</span><br/>
              <span style="font-size: 11px; color: #999;">${connections} connection${connections !== 1 ? 's' : ''}</span><br/>
              <em style="font-size: 11px; color: #999;">Click for details</em>
            </div>`;
          }
        }
      };
    });

    const edges: any[] = this.graphData.edges.map(edge => ({
      from: edge.from,
      to: edge.to,
      label: {
        enabled: false  // Disable edge labels
      },
      tooltip: {
        useHtml: true,
        format: () => {
          const detailedDesc = edge.detailedDescription || edge.label;
          return `<div style="padding: 8px; max-width: 300px;">
            <strong style="font-size: 12px;">${edge.label}</strong><br/>
            <span style="font-size: 11px; color: #666;">${detailedDesc}</span><br/>
            <em style="font-size: 10px; color: #999;">Click for details</em>
          </div>`;
        }
      }
    }));

    // Create graph
    this.chart = anychart.graph();
    const data = {
      nodes: nodes,
      edges: edges
    };

    this.chart.data(data);

    // Configure layout
    this.chart.layout().type('force-directed');
    this.chart.layout().iterationCount(100);

    // Configure nodes
    this.chart.nodes().normal().fill('#4285f4');
    this.chart.nodes().hovered().fill('#1967d2');
    this.chart.nodes().selected().fill('#ea4335');
    this.chart.nodes().normal().stroke(null);
    this.chart.nodes().labels().enabled(true);
    this.chart.nodes().labels().fontSize(12);
    this.chart.nodes().labels().fontWeight('bold');
    this.chart.nodes().tooltip().useHtml(true);

    // Configure edges
    this.chart.edges().normal().stroke('#9aa0a6', 2);  // Increased thickness for easier clicking
    this.chart.edges().hovered().stroke('#1967d2', 4);
    this.chart.edges().selected().stroke('#ea4335', 4);
    this.chart.edges().labels().enabled(false);  // Disable all edge labels
    this.chart.edges().tooltip().useHtml(true);

    // Make edges interactive and clickable
    this.chart.interactivity().edges(true);

    // Add click listeners
    this.chart.listen('click', (e: any) => {
      console.log('Click event:', e);
      console.log('domTarget:', e.domTarget);
      console.log('domTarget.tag:', e.domTarget?.tag);
      console.log('tag.type:', e.domTarget?.tag?.type);

      if (e.domTarget && e.domTarget.tag) {
        const tagType = e.domTarget.tag.type;
        console.log(`Detected click on: ${tagType}`);

        if (tagType === 'node') {
          this.onNodeClick(e.domTarget.tag.id);
        } else if (tagType === 'edge') {
          console.log('Edge tag:', e.domTarget.tag);
          // Parse edge index from ID (format: "edge_0", "edge_1", etc.)
          const edgeId = e.domTarget.tag.id;
          const edgeIndex = parseInt(edgeId.split('_')[1], 10);

          console.log('Edge ID:', edgeId, 'Parsed index:', edgeIndex);

          if (!isNaN(edgeIndex) && this.graphData && this.graphData.edges[edgeIndex]) {
            const edge = this.graphData.edges[edgeIndex];
            console.log('Found edge at index', edgeIndex, ':', edge);
            this.onEdgeClick(edge.from, edge.to);
          } else {
            console.warn('Edge index not found or invalid. ID:', edgeId, 'Index:', edgeIndex);
          }
        } else {
          console.warn('Unknown tag type:', tagType);
        }
      } else {
        console.warn('No domTarget or tag found in click event');
      }
    });

    // Set chart title
    this.chart.title(`Character Relationships - ${this.data.bookTitle}`);
    this.chart.title().fontColor('#202124');
    this.chart.title().fontSize(16);

    // Draw
    this.chart.container(container);
    this.chart.draw();

    console.log('✅ Graph rendered');
  }

  onNodeClick(nodeId: string): void {
    this.selectedNodeId = nodeId;
    this.selectedEdge = null;
    this.detailsVisible = true;
    console.log('Node clicked:', nodeId);
  }

  onEdgeClick(from: string, to: string): void {
    this.selectedEdge = { from, to };
    this.selectedNodeId = null;
    this.detailsVisible = true;
    console.log('Edge clicked:', from, '->', to);
    console.log('Available edges:', this.graphData?.edges);
  }

  getSelectedNode() {
    if (!this.selectedNodeId || !this.graphData) return null;
    return this.graphData.nodes.find(n => n.id === this.selectedNodeId);
  }

  getNodeLabel(nodeId: string): string {
    if (!this.graphData) return nodeId;
    const node = this.graphData.nodes.find(n => n.id === nodeId);
    return node ? this.formatCharacterName(node.label) : nodeId;
  }

  getSelectedEdge() {
    if (!this.selectedEdge || !this.graphData) {
      return null;
    }

    console.log('Looking for edge:', this.selectedEdge);

    // Find edge - check both directions since edges are bidirectional
    const edge = this.graphData.edges.find(e => {
      const forwardMatch = e.from === this.selectedEdge!.from && e.to === this.selectedEdge!.to;
      const reverseMatch = e.from === this.selectedEdge!.to && e.to === this.selectedEdge!.from;
      return forwardMatch || reverseMatch;
    });

    if (edge) {
      console.log('Found edge:', edge);
    } else {
      console.warn('Edge not found! Looking for:', this.selectedEdge);
      console.log('Available edges:', this.graphData.edges.map(e => ({ from: e.from, to: e.to, label: e.label })));
    }

    return edge || null;
  }

  closeDetails(): void {
    this.detailsVisible = false;
    this.selectedNodeId = null;
    this.selectedEdge = null;
  }

  regenerateGraph(): void {
    if (confirm('This will regenerate the character graph. Continue?')) {
      this.generateGraph();
    }
  }

  close(): void {
    this.dialogRef.close();
  }
}

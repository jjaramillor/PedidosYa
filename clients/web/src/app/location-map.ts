import {
  AfterViewInit,
  Component,
  ElementRef,
  inject,
  input,
  OnChanges,
  OnDestroy,
  output,
  viewChild,
} from '@angular/core';
import type * as L from 'leaflet';
import { Api } from './api';

export interface Point {
  latitude: number;
  longitude: number;
}
@Component({
  selector: 'location-map',
  template: `<div #canvas class="map" role="application" aria-label="Mapa de ubicación"></div>`,
  styles: `
    .map {
      height: 320px;
      border-radius: 12px;
      border: 1px solid #b7c6bc;
      margin: 16px 0;
    }
  `,
})
export class LocationMap implements AfterViewInit, OnChanges, OnDestroy {
  point = input.required<Point>();
  courier = input<Point | null>(null);
  editable = input(false);
  selected = output<Point>();
  private canvas = viewChild.required<ElementRef<HTMLDivElement>>('canvas');
  private api = inject(Api);
  private map?: L.Map;
  private marker?: L.CircleMarker;
  private moving?: L.CircleMarker;
  private destroyed = false;
  private leaflet?: typeof import('leaflet');
  async ngAfterViewInit() {
    this.leaflet = await import('leaflet');
    if (this.destroyed) return;
    const L = this.leaflet;
    this.map = L.map(this.canvas().nativeElement).setView(
      [this.point().latitude, this.point().longitude],
      14,
    );
    this.map.on('click', (event: L.LeafletMouseEvent) => {
      if (this.editable())
        this.selected.emit({ latitude: event.latlng.lat, longitude: event.latlng.lng });
    });
    this.draw();
    try {
      const settings = await this.api.call('/maps/settings');
      if (!this.destroyed && settings.tilesUrl)
        L.tileLayer(settings.tilesUrl, { attribution: settings.attribution, maxZoom: 19 }).addTo(
          this.map!,
        );
    } catch {
      /* Location markers remain usable when the base map is unavailable. */
    }
  }
  ngOnChanges() {
    if (this.map) this.draw();
  }
  private draw() {
    const L = this.leaflet;
    if (!this.map || !L) return;
    const point = this.point();
    const position: L.LatLngTuple = [point.latitude, point.longitude];
    if (!this.marker)
      this.marker = L.circleMarker(position, { color: '#244c3d', fillOpacity: 1, radius: 8 })
        .bindTooltip('Dirección de entrega')
        .addTo(this.map);
    else this.marker.setLatLng(position);
    const courier = this.courier();
    if (courier) {
      const location: L.LatLngTuple = [courier.latitude, courier.longitude];
      if (!this.moving)
        this.moving = L.circleMarker(location, { color: '#e9663e', fillOpacity: 1, radius: 8 })
          .bindTooltip('Repartidor')
          .addTo(this.map);
      else this.moving.setLatLng(location);
      this.map.fitBounds(L.latLngBounds([position, location]), { padding: [40, 40], maxZoom: 15 });
    } else {
      this.moving?.remove();
      this.moving = undefined;
      this.map.setView(position, this.map.getZoom());
    }
  }
  ngOnDestroy() {
    this.destroyed = true;
    this.map?.remove();
  }
}

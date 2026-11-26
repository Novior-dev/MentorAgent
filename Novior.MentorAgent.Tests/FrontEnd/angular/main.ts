// MentorAgent test fixture (story 1642): a real Angular application, built ahead of time for production, with a reactive
// form, its Routes as an application gives them to provideRouter, and nothing else. The test page loads it, then
// MentorAgent's /mentor/agent.js, and ties the recipes to its own hub connection.
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Routes } from '@angular/router';

@Component({
  selector: 'app-order',
  imports: [ReactiveFormsModule],
  template: `
    <form [formGroup]="form" (ngSubmit)="send()">
      <input id="customer" formControlName="customer" aria-label="Customer">
      <select id="product" formControlName="product" aria-label="Product">
        <option value="">Choose a product</option>
        <option value="pen">Fountain pen</option>
        <option value="ink">Blue ink</option>
      </select>
      <input id="gift" type="checkbox" formControlName="gift" aria-label="Gift">
      <input id="note" formControlName="note" aria-label="Note">
      <span id="state">{{ form.controls.customer.value }}</span>
      <button id="send" type="submit" [disabled]="form.invalid">Send</button>
    </form>
    @if (sent(); as order) {
      <pre id="sent">{{ order }}</pre>
    }
  `,
})
export class OrderForm {
  readonly form = new FormGroup({
    customer: new FormControl('', { nonNullable: true, validators: Validators.required }),
    product: new FormControl('', { nonNullable: true, validators: Validators.required }),
    gift: new FormControl(false, { nonNullable: true }),
    note: new FormControl('', { nonNullable: true, updateOn: 'blur' }),
  });
  readonly sent = signal<string | null>(null);

  send(): void {
    this.sent.set(JSON.stringify({ value: this.form.getRawValue(), touched: this.form.controls.customer.touched }));
  }
}

@Component({ selector: 'app-detail', template: '' })
export class OrderDetail {}

@Component({ selector: 'app-missing', template: '' })
export class NotFound {}

/** The routes as an application writes them, for attach's options.routes. */
export const routes: Routes = [
  { path: '', redirectTo: 'orders', pathMatch: 'full' },
  { path: 'orders', children: [
    { path: '', component: OrderForm },
    { path: ':id', component: OrderDetail },
  ] },
  { path: '**', component: NotFound },
];

(window as unknown as { angular: unknown }).angular = {
  routes,
  ready: bootstrapApplication(OrderForm, { providers: [provideZonelessChangeDetection()] }).then(() => true),
};
